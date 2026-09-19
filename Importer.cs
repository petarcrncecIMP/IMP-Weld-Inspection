using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace IMPWeldPhotos;

public sealed record ImportProgress(int FilesDone, int FilesTotal, long BytesDone, long BytesTotal, string CurrentName);

/// <summary>A card file that is safely at the destination, as it was when imported.
/// Clear card deletes only these, and only if unchanged.</summary>
public sealed record VerifiedSource(string Path, long Length, DateTime LastWriteUtc);

public sealed record ImportFailure(int BomCode, string WeldLabel, string Source, string Reason);

public sealed class ImportResult
{
    public int Copied { get; set; }
    public int Duplicates { get; set; }
    public int UnresolvedCopied { get; set; }
    public int MovedFromUnresolved { get; set; }
    public List<ImportFailure> Failures { get; } = new();
    public NoteList Notes { get; } = new();
    public SortedSet<int> UnresolvedBoms { get; } = new();
    public List<VerifiedSource> Verified { get; } = new();
    public HashSet<string> DestinationFolders { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool Cancelled { get; set; }
    public bool CardRemoved { get; set; }
    public string? LogPath { get; set; }

    public bool FullyVerified => !Cancelled && !CardRemoved && Failures.Count == 0;
}

public sealed class CardRemovedException : Exception
{
}

/// <summary>
/// Copies one plan to the share, unstamped: the photos on U: are the endoscope's own files
/// (stamping happens only when a report is made). Per photo: hash the source, skip it when a
/// photo of the same weld at the destination has that hash, copy it to {target}.part, check
/// the copy's hash, rename it into place. Any failure removes the .part.
/// </summary>
public sealed class Importer
{
    private static readonly Regex StoredName = new(@"^\d{7}-.+-\d+\.[^.]+$", RegexOptions.CultureInvariant);

    private readonly AppConfig _cfg;
    private readonly BomResolver _resolver;
    private readonly IProgress<ImportProgress>? _progress;
    private readonly DestinationTree _tree;
    private readonly ImportResult _result = new();
    private CsvLog _log = null!;
    private string _cardRoot = "";
    private int _filesDone, _filesTotal;
    private long _bytesDone, _bytesTotal;

    private Importer(AppConfig cfg, BomResolver resolver, IProgress<ImportProgress>? progress)
    {
        _cfg = cfg;
        _resolver = resolver;
        _progress = progress;
        _tree = new DestinationTree(cfg.DestinationRoot);
    }

    public static Task<ImportResult> RunAsync(ImportPlan plan, AppConfig cfg, BomResolver resolver,
                                              IProgress<ImportProgress>? progress, CancellationToken ct) =>
        new Importer(cfg, resolver, progress).RunAsync(plan, ct);

    private async Task<ImportResult> RunAsync(ImportPlan plan, CancellationToken ct)
    {
        _cardRoot = plan.Scan.Root;
        _filesTotal = plan.Welds.Sum(w => w.Photos);
        _bytesTotal = plan.Welds.Sum(w => w.Bytes);

        Directory.CreateDirectory(AppConfig.LogFolder);
        _result.LogPath = Path.Combine(AppConfig.LogFolder, $"uvoz_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
        using var log = _log = new CsvLog(_result.LogPath);

        try
        {
            await SweepUnresolvedAsync(ct);
            foreach (var weld in plan.Welds)
            {
                ct.ThrowIfCancellationRequested();
                await ImportWeldAsync(weld, ct);
            }
        }
        catch (OperationCanceledException)
        {
            _result.Cancelled = true;
            _result.Notes.Add("Uvoz je bil preklican. Kar je navedeno kot kopirano, je shranjeno.");
        }
        catch (CardRemovedException)
        {
            _result.CardRemoved = true;
            _result.Notes.Add("Kartica je bila odstranjena med uvozom. Uvoz je ustavljen; kar je navedeno kot kopirano, je shranjeno.");
        }
        return _result;
    }

    // ─── One weld ────────────────────────────────────────────────────────────

    private async Task ImportWeldAsync(WeldPlan weld, CancellationToken ct)
    {
        string folder;
        try
        {
            if (weld.Resolution.Info is { } info)
            {
                folder = _tree.EnsureIsoFolder(info, _result.Notes);
            }
            else
            {
                folder = weld.DestinationFolder;
                Directory.CreateDirectory(folder);
            }
        }
        catch (Exception ex)
        {
            FailAll(weld, $"ciljne mape ni mogoče pripraviti: {ex.Message}");
            return;
        }

        // Photos are copied byte for byte, so a card photo is already in exactly when a photo
        // of this weld at the destination has the same SHA-256. Deleted photos simply aren't
        // there any more and come back on the next import.
        var prefix = weld.FilePrefix;
        Dictionary<string, string> imported;
        try
        {
            imported = await HashFilesAsync(Numbering.WeldFiles(folder, prefix), ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            FailAll(weld, $"obstoječih fotografij v {folder} ni mogoče prebrati: {ex.Message}");
            return;
        }
        var next = Numbering.Highest(folder, prefix) + 1;

        // Capture time, then original name, so -1 is the first shot.
        var ordered = new List<(FileInfo File, DateTime Sort)>();
        foreach (var file in weld.Files)
        {
            ct.ThrowIfCancellationRequested();
            DateTime? exif = null;
            try
            {
                file.Refresh();
                if (MediaFiles.IsImage(file.Name) && file.Exists) exif = MediaFiles.ReadDateTimeOriginal(file.FullName);
            }
            catch (Exception) when (!CardPresent())
            {
                throw new CardRemovedException();
            }
            catch (Exception)
            {
                // Unreadable metadata only costs the ordering; the import reports the file.
            }
            ordered.Add((file, exif ?? SafeLastWrite(file)));
        }

        foreach (var (file, _) in ordered.OrderBy(o => o.Sort).ThenBy(o => o.File.Name, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            Report(file.Name);
            next = await ImportFileAsync(weld, file, folder, imported, next, ct);
        }
    }

    /// <summary>Returns the next free n. imported maps SHA-256 to the destination file name.</summary>
    private async Task<int> ImportFileAsync(WeldPlan weld, FileInfo file, string folder,
                                            Dictionary<string, string> imported, int next, CancellationToken ct)
    {
        var ext = file.Extension.ToLowerInvariant();
        string hash;
        long length;
        DateTime lastWrite;
        try
        {
            file.Refresh();
            lastWrite = file.LastWriteTimeUtc;
            length = file.Length;
            hash = await HashAsync(file.FullName, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception) when (!CardPresent())
        {
            throw new CardRemovedException();
        }
        catch (Exception ex)
        {
            Fail(weld, file, "", "", $"izvorne datoteke ni mogoče prebrati: {ex.Message}");
            return next;
        }

        if (imported.TryGetValue(hash, out var existingName))
        {
            _result.Duplicates++;
            _result.Verified.Add(new VerifiedSource(file.FullName, length, lastWrite));
            _log.Row(file.FullName, Path.Combine(folder, existingName), hash, "preskočena - že uvožena");
            Done(length);
            return next;
        }

        int n;
        string name, final, part;
        for (n = next; ; n++)
        {
            name = $"{weld.FilePrefix}-{n}{ext}";
            final = Path.Combine(folder, name);
            part = final + ".part";
            if (!File.Exists(final) && !File.Exists(part)) break;
        }

        var step = "kopiranje";
        try
        {
            await CopyPartAsync(file.FullName, part, ct);
            // Read the copy back: the bytes on the share must be the card's bytes.
            step = "preverjanje kopije";
            if (!string.Equals(await HashAsync(part, ct), hash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("kopija se ne ujema z izvirnikom");
            step = "preimenovanje v končno ime";
            await ShareRetry.RunAsync(() => File.Move(part, final), ct);
        }
        catch (Exception ex)
        {
            TryDelete(part);
            if (ex is OperationCanceledException) throw;
            if (!CardPresent()) throw new CardRemovedException();
            Fail(weld, file, final, hash, $"{step}: {ex.Message}");
            return n;
        }

        imported[hash] = name;
        if (weld.Resolution.Info == null)
        {
            _result.UnresolvedCopied++;
            _result.UnresolvedBoms.Add(weld.BomCode);
        }
        else
        {
            _result.Copied++;
        }
        _result.Verified.Add(new VerifiedSource(file.FullName, length, lastWrite));
        _result.DestinationFolders.Add(folder);
        _log.Row(file.FullName, final, hash, weld.Resolution.Info == null ? "kopirana - nerazvrščena" : "kopirana");
        Done(length);
        return n + 1;
    }

    // ─── _Nerazvrsceno ───────────────────────────────────────────────────────

    /// <summary>Photos parked under _Nerazvrsceno move to their isometrija folder once
    /// the BOM resolves, keeping their names.</summary>
    private async Task SweepUnresolvedAsync(CancellationToken ct)
    {
        var parked = Path.Combine(_cfg.DestinationRoot, DestinationTree.UnresolvedFolderName);
        if (!Directory.Exists(parked)) return;

        var codes = Directory.GetDirectories(parked)
            .Select(Path.GetFileName)
            .Where(n => n is { Length: 7 } && n.All(char.IsDigit))
            .Select(n => int.Parse(n!))
            .ToList();
        await _resolver.PrefetchAsync(codes, ct);

        foreach (var bomCode in codes)
        {
            ct.ThrowIfCancellationRequested();
            var bomDir = Path.Combine(parked, bomCode.ToString());
            var res = await _resolver.ResolveAsync(bomCode, ct);
            if (res.Info == null) continue;
            try
            {
                await MoveResolvedAsync(bomDir, res.Info, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _result.Notes.Add($"Nerazvrščenih fotografij izometrije {bomCode} ni mogoče premakniti: {ex.Message}");
            }
        }
        TryDeleteEmptyDirectory(parked);
    }

    private async Task MoveResolvedAsync(string bomDir, BomInfo info, CancellationToken ct)
    {
        var dest = _tree.EnsureIsoFolder(info, _result.Notes);
        foreach (var path in Directory.GetFiles(bomDir))
        {
            var name = Path.GetFileName(path);
            if (name.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(path); // left by a crash; never a finished photo
                continue;
            }
            if (!StoredName.IsMatch(name)) continue;

            var to = Path.Combine(dest, name);
            if (File.Exists(to))
            {
                if (await HashAsync(path, ct) == await HashAsync(to, ct))
                {
                    TryDelete(path);
                    _log.Row(path, to, "", "odstranjena iz _Nerazvrsceno - enaka je že na mestu");
                }
                else
                {
                    _result.Notes.Add($"{name} je že v {dest}; ostane v {bomDir}.");
                }
                continue;
            }

            await ShareRetry.RunAsync(() => File.Move(path, to), ct);
            _result.MovedFromUnresolved++;
            _result.DestinationFolders.Add(dest);
            _log.Row(path, to, "", "premaknjena iz _Nerazvrsceno");
        }

        // A hidden .imported.json from versions before 1.0.7 may be all that is left.
        foreach (var leftover in Directory.GetFiles(bomDir, ".imported.json*")) TryDelete(leftover);
        TryDeleteEmptyDirectory(bomDir);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private bool CardPresent() => Directory.Exists(_cardRoot);

    private static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var stream = OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
    }

    /// <summary>SHA-256 -> file name for the given files.</summary>
    private static async Task<Dictionary<string, string>> HashFilesAsync(IEnumerable<string> paths, CancellationToken ct)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths) map.TryAdd(await HashAsync(path, ct), Path.GetFileName(path));
        return map;
    }

    private static async Task CopyPartAsync(string source, string part, CancellationToken ct)
    {
        await using var from = OpenRead(source);
        await using var to = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.Asynchronous);
        await from.CopyToAsync(to, 1 << 20, ct);
        await to.FlushAsync(ct);
    }

    private void Fail(WeldPlan weld, FileInfo file, string destination, string hash, string reason)
    {
        _result.Failures.Add(new ImportFailure(weld.BomCode, weld.WeldLabel, file.FullName, reason));
        _log.Row(file.FullName, destination, hash, "napaka: " + reason);
        Done(SafeLength(file));
    }

    private void FailAll(WeldPlan weld, string reason)
    {
        foreach (var file in weld.Files) Fail(weld, file, weld.DestinationFolder, "", reason);
    }

    private void Report(string name) =>
        _progress?.Report(new ImportProgress(_filesDone, _filesTotal, _bytesDone, _bytesTotal, name));

    private void Done(long bytes)
    {
        _filesDone++;
        _bytesDone += bytes;
        Report("");
    }

    private static long SafeLength(FileInfo f)
    {
        try
        {
            return f.Length;
        }
        catch
        {
            return 0;
        }
    }

    private static DateTime SafeLastWrite(FileInfo f)
    {
        try
        {
            return f.LastWriteTime;
        }
        catch
        {
            return DateTime.MaxValue;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
        }
        catch
        {
        }
    }

    private static void TryDeleteEmptyDirectory(string dir)
    {
        try
        {
            if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
        }
        catch
        {
        }
    }
}

/// <summary>On a network share, antivirus or indexing opens a freshly written file for a
/// moment, and renaming or replacing it then fails with "Access to the path is denied".
/// Those locks clear quickly, so file renames try again for about four seconds.</summary>
public static class ShareRetry
{
    private static readonly int[] Delays = { 100, 250, 500, 1000, 2000 };

    public static async Task RunAsync(Action action, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < Delays.Length)
            {
                await Task.Delay(Delays[attempt], ct);
            }
        }
    }
}

/// <summary>Per-import log: time, source, destination, source SHA-256, result.</summary>
public sealed class CsvLog : IDisposable
{
    private readonly StreamWriter _writer;

    public CsvLog(string path)
    {
        _writer = new StreamWriter(path, false, new UTF8Encoding(true)) { AutoFlush = true };
        _writer.WriteLine("time,source,destination,source_sha256,result");
    }

    public void Row(string source, string destination, string sha256, string result) =>
        _writer.WriteLine(string.Join(",",
            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            Escape(source), Escape(destination), sha256, Escape(result)));

    private static string Escape(string s) =>
        s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0 ? s : "\"" + s.Replace("\"", "\"\"") + "\"";

    public void Dispose() => _writer.Dispose();
}

public static class CardCleaner
{
    public sealed record Outcome(int Deleted, IReadOnlyList<string> Problems);

    /// <summary>Deletes the card files an import verified, and only those still
    /// exactly as they were. Folders stay, so the card is ready for the next job.</summary>
    public static Outcome Clear(IEnumerable<VerifiedSource> files)
    {
        var deleted = 0;
        var problems = new List<string>();
        foreach (var f in files.DistinctBy(f => f.Path, StringComparer.OrdinalIgnoreCase))
        {
            var fi = new FileInfo(f.Path);
            if (!fi.Exists) continue;
            if (fi.Length != f.Length || fi.LastWriteTimeUtc != f.LastWriteUtc)
            {
                problems.Add($"{f.Path} se je po uvozu spremenila, zato ni izbrisana.");
                continue;
            }
            try
            {
                if (fi.IsReadOnly) fi.IsReadOnly = false;
                fi.Delete();
                deleted++;
            }
            catch (Exception ex)
            {
                problems.Add($"{f.Path}: {ex.Message}");
            }
        }
        return new Outcome(deleted, problems);
    }
}
