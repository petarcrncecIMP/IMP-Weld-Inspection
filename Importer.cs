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
/// Copies one plan to the share. Per photo: read and hash the source, skip it if the
/// manifest already has that hash for the weld, write the stamped image (or the
/// video) to {target}.part, check it, rename it into place, record it. Any failure
/// removes the .part and leaves no manifest entry.
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

        Manifest manifest;
        try
        {
            manifest = Manifest.Load(folder);
        }
        catch (Exception ex)
        {
            FailAll(weld, $"{Manifest.FileName} v {folder} ni berljiv: {ex.Message}");
            return;
        }
        try
        {
            if (manifest.DropMissing()) await RetryAsync(manifest.Save, ct);
        }
        catch (Exception ex)
        {
            FailAll(weld, $"{Manifest.FileName} v {folder} ni mogoče posodobiti: {ex.Message}");
            return;
        }

        var prefix = weld.FilePrefix;
        var importedHashes = manifest.Files
            .Where(kv => kv.Key.StartsWith(prefix + "-", StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var next = Numbering.Highest(folder, manifest, prefix) + 1;

        // Capture time, then original name, so -1 is the first shot.
        var ordered = new List<(FileInfo File, DateTime? Exif, DateTime Sort)>();
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
            ordered.Add((file, exif, exif ?? SafeLastWrite(file)));
        }

        foreach (var (file, exif, _) in ordered.OrderBy(o => o.Sort).ThenBy(o => o.File.Name, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            Report(file.Name);
            next = await ImportFileAsync(weld, file, exif, folder, manifest, importedHashes, next, ct);
        }
    }

    /// <summary>Returns the next free n.</summary>
    private async Task<int> ImportFileAsync(WeldPlan weld, FileInfo file, DateTime? exif, string folder,
                                            Manifest manifest, HashSet<string> importedHashes, int next,
                                            CancellationToken ct)
    {
        var isImage = MediaFiles.IsImage(file.Name);
        var ext = file.Extension.ToLowerInvariant();

        byte[]? bytes = null;
        string hash;
        long length;
        DateTime lastWrite;
        try
        {
            file.Refresh();
            lastWrite = file.LastWriteTimeUtc;
            if (isImage)
            {
                bytes = await File.ReadAllBytesAsync(file.FullName, ct);
                length = bytes.Length;
                hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            }
            else
            {
                length = file.Length;
                await using var stream = OpenSource(file.FullName);
                hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
            }
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
            Fail(weld, file, "", "", isImage, $"izvorne datoteke ni mogoče prebrati: {ex.Message}");
            return next;
        }

        if (importedHashes.Contains(hash))
        {
            _result.Duplicates++;
            _result.Verified.Add(new VerifiedSource(file.FullName, length, lastWrite));
            var existingName = manifest.Files.FirstOrDefault(kv =>
                string.Equals(kv.Value, hash, StringComparison.OrdinalIgnoreCase) &&
                kv.Key.StartsWith(weld.FilePrefix + "-", StringComparison.OrdinalIgnoreCase)).Key;
            _log.Row(file.FullName, existingName == null ? "" : Path.Combine(folder, existingName), hash, false,
                     "preskočena - že uvožena");
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
            if (!File.Exists(final) && !File.Exists(part) && !manifest.Files.ContainsKey(name)) break;
        }

        var moved = false;
        var step = "zapis fotografije";
        try
        {
            if (bytes != null)
            {
                var stamped = PhotoStamper.Stamp(bytes, Path.GetFileNameWithoutExtension(name), ext, exif);
                await WritePartAsync(part, stamped.Bytes, ct);
                step = "preverjanje zapisa";
                if (!PhotoStamper.Verify(part, stamped.Width, stamped.Height, stamped.Bytes.Length))
                    throw new IOException("zapisane slike ni mogoče prebrati nazaj");
            }
            else
            {
                await CopyPartAsync(file.FullName, part, ct);
                step = "preverjanje zapisa";
                if (new FileInfo(part).Length != length)
                    throw new IOException("velikost kopije se ne ujema z izvorom");
            }

            step = "preimenovanje v končno ime";
            await RetryAsync(() => File.Move(part, final), ct);
            moved = true;
            manifest.Files[name] = hash;
            step = $"zapis v {Manifest.FileName}";
            await RetryAsync(manifest.Save, ct);
        }
        catch (Exception ex)
        {
            TryDelete(part);
            if (moved)
            {
                // Photo without a manifest entry would be imported again next time.
                manifest.Files.Remove(name);
                TryDelete(final);
            }
            if (ex is OperationCanceledException) throw;
            if (!CardPresent()) throw new CardRemovedException();
            Fail(weld, file, final, hash, isImage, $"{step}: {ex.Message}");
            return n;
        }

        importedHashes.Add(hash);
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
        _log.Row(file.FullName, final, hash, bytes != null,
                 weld.Resolution.Info == null ? "kopirana - nerazvrščena" : "kopirana");
        Done(length);
        return n + 1;
    }

    // ─── _Nerazvrsceno ───────────────────────────────────────────────────────

    /// <summary>Photos parked under _Nerazvrsceno move to their isometrija folder once
    /// the BOM resolves, keeping their names and manifest entries.</summary>
    private async Task SweepUnresolvedAsync(CancellationToken ct)
    {
        var parked = Path.Combine(_cfg.DestinationRoot, DestinationTree.UnresolvedFolderName);
        if (!Directory.Exists(parked)) return;

        foreach (var bomDir in Directory.GetDirectories(parked))
        {
            ct.ThrowIfCancellationRequested();
            var dirName = Path.GetFileName(bomDir);
            if (dirName.Length != 7 || !int.TryParse(dirName, out var bomCode)) continue;

            var res = await _resolver.ResolveAsync(bomCode, ct);
            if (res.Info == null) continue;
            try
            {
                MoveResolved(bomDir, res.Info);
            }
            catch (Exception ex)
            {
                _result.Notes.Add($"Nerazvrščenih fotografij izometrije {bomCode} ni mogoče premakniti: {ex.Message}");
            }
        }
        TryDeleteEmptyDirectory(parked);
    }

    private void MoveResolved(string bomDir, BomInfo info)
    {
        var dest = _tree.EnsureIsoFolder(info, _result.Notes);
        var source = Manifest.Load(bomDir);
        var target = Manifest.Load(dest);
        target.DropMissing();

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
            if (File.Exists(to) || target.Files.ContainsKey(name))
            {
                _result.Notes.Add($"{name} je že v {dest}; ostane v {bomDir}.");
                continue;
            }

            File.Move(path, to);
            source.Files.Remove(name, out var hash);
            if (hash != null)
            {
                target.Files[name] = hash;
                target.Save();
                source.Save();
            }
            _result.MovedFromUnresolved++;
            _result.DestinationFolders.Add(dest);
            _log.Row(path, to, hash ?? "", false, "premaknjena iz _Nerazvrsceno");
        }

        var leftovers = Directory.EnumerateFiles(bomDir)
            .Any(f => !Path.GetFileName(f).Equals(Manifest.FileName, StringComparison.OrdinalIgnoreCase));
        if (source.Files.Count == 0 && !leftovers)
        {
            source.Delete();
            TryDeleteEmptyDirectory(bomDir);
        }
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private bool CardPresent() => Directory.Exists(_cardRoot);

    /// <summary>On a network share, antivirus or indexing opens a freshly written file for a
    /// moment, and renaming or replacing it then fails with "Access to the path is denied".
    /// Those locks clear quickly, so the steps that rename files try again for about four
    /// seconds before giving up.</summary>
    private static async Task RetryAsync(Action action, CancellationToken ct)
    {
        int[] delays = { 100, 250, 500, 1000, 2000 };
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < delays.Length)
            {
                await Task.Delay(delays[attempt], ct);
            }
        }
    }

    private static FileStream OpenSource(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static async Task WritePartAsync(string part, byte[] bytes, CancellationToken ct)
    {
        await using var fs = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous);
        await fs.WriteAsync(bytes, ct);
        await fs.FlushAsync(ct);
    }

    private static async Task CopyPartAsync(string source, string part, CancellationToken ct)
    {
        await using var from = OpenSource(source);
        await using var to = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.Asynchronous);
        await from.CopyToAsync(to, 1 << 20, ct);
        await to.FlushAsync(ct);
    }

    private void Fail(WeldPlan weld, FileInfo file, string destination, string hash, bool stamped, string reason)
    {
        _result.Failures.Add(new ImportFailure(weld.BomCode, weld.WeldLabel, file.FullName, reason));
        _log.Row(file.FullName, destination, hash, stamped, "napaka: " + reason);
        Done(SafeLength(file));
    }

    private void FailAll(WeldPlan weld, string reason)
    {
        foreach (var file in weld.Files) Fail(weld, file, weld.DestinationFolder, "", false, reason);
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

/// <summary>Per-import log: time, source, destination, source SHA-256, stamped, result.</summary>
public sealed class CsvLog : IDisposable
{
    private readonly StreamWriter _writer;

    public CsvLog(string path)
    {
        _writer = new StreamWriter(path, false, new UTF8Encoding(true)) { AutoFlush = true };
        _writer.WriteLine("time,source,destination,source_sha256,stamped,result");
    }

    public void Row(string source, string destination, string sha256, bool stamped, string result) =>
        _writer.WriteLine(string.Join(",",
            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            Escape(source), Escape(destination), sha256, stamped ? "yes" : "no", Escape(result)));

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
