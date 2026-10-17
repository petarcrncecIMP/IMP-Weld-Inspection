using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace IMPWeldPhotos;

/// <summary>
/// Keeps the card and the share in step when a photo is moved to another weld in Pregled.
/// Without it the card would still hold the photo under the old weld, and the two would drift
/// apart (the import would leave it where it is, but the card would say something else).
///
/// The import copies byte for byte, so a photo on the share and its original on the card have
/// the same SHA-256; that is how the card's copy is found again.
/// </summary>
public static class CardSync
{
    private static readonly Regex WeldFolderRx = new(@"^(\d{3})_(F?W\d+(\.\d)?[a-z]?)$", RegexOptions.CultureInvariant);

    /// <summary>This isometrija's folder on an inserted card, or null when no card has it.
    /// Roots are the removable drives unless given (the tests give their own).</summary>
    public static string? FindIsoFolder(string bomCode, IEnumerable<string>? roots = null)
    {
        if (bomCode.Length == 0) return null;
        foreach (var root in roots ?? DriveWatcher.ReadyRemovableDrives())
        {
            try
            {
                var folder = Path.Combine(root, bomCode);
                if (Directory.Exists(folder) && Directory.EnumerateDirectories(folder).Any()) return folder;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // An unreadable drive is simply not the card we want.
            }
        }
        return null;
    }

    /// <summary>The card's isometrija folder with every photo hashed once, so a move of many
    /// photos reads the card once rather than once per photo.</summary>
    public sealed class CardIndex
    {
        internal CardIndex(string folder, Dictionary<string, string> byHash)
        {
            Folder = folder;
            ByHash = byHash;
        }

        public string Folder { get; }

        /// <summary>SHA-256 → the card file's path.</summary>
        internal Dictionary<string, string> ByHash { get; }

        public int Photos => ByHash.Count;
    }

    public static async Task<CardIndex> IndexAsync(string isoFolderOnCard, CancellationToken ct)
    {
        var byHash = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var weldFolder in Directory.GetDirectories(isoFolderOnCard))
        {
            if (!WeldFolderRx.IsMatch(Path.GetFileName(weldFolder))) continue;
            foreach (var file in Directory.GetFiles(weldFolder))
            {
                ct.ThrowIfCancellationRequested();
                if (!MediaFiles.IsImportable(new FileInfo(file))) continue;
                byHash.TryAdd(await HashAsync(file, ct), file);
            }
        }
        return new CardIndex(isoFolderOnCard, byHash);
    }

    /// <summary>Moves the card's copy of this photo into the target weld's folder, making that
    /// folder when the card hasn't got one. Returns where it went, or null when the card hasn't
    /// got the photo (already cleared, or it came from another card).</summary>
    public static async Task<string?> MovePhotoAsync(CardIndex card, string serverPhotoPath, string weldLabel, CancellationToken ct)
    {
        var hash = await HashAsync(serverPhotoPath, ct);
        if (!card.ByHash.TryGetValue(hash, out var file) || !File.Exists(file)) return null;

        var target = Path.Combine(EnsureWeldFolder(card.Folder, weldLabel), Path.GetFileName(file));
        if (string.Equals(target, file, StringComparison.OrdinalIgnoreCase)) return file;
        target = FreeName(target);
        await ShareRetry.RunAsync(() => File.Move(file, target), ct);
        card.ByHash[hash] = target;
        return target;
    }

    /// <summary>Deletes the card's copy of a photo, by the hash taken before the share's copy
    /// went. Returns the path it had, or null when the card hadn't got it.</summary>
    public static async Task<string?> DeleteCopyAsync(CardIndex card, string hash, CancellationToken ct)
    {
        if (!card.ByHash.TryGetValue(hash, out var file) || !File.Exists(file)) return null;
        await ShareRetry.RunAsync(() =>
        {
            File.SetAttributes(file, FileAttributes.Normal);
            File.Delete(file);
        }, ct);
        card.ByHash.Remove(hash);
        return file;
    }

    /// <summary>One photo, indexing the card for it; for several, index once and reuse it.</summary>
    public static async Task<string?> MovePhotoAsync(string isoFolderOnCard, string serverPhotoPath,
                                                     string weldLabel, CancellationToken ct) =>
        await MovePhotoAsync(await IndexAsync(isoFolderOnCard, ct), serverPhotoPath, weldLabel, ct);

    /// <summary>The card's folder for that weld, made as {next NNN}_{label} when there is none.</summary>
    private static string EnsureWeldFolder(string isoFolderOnCard, string weldLabel)
    {
        var highest = 0;
        foreach (var folder in Directory.GetDirectories(isoFolderOnCard))
        {
            var m = WeldFolderRx.Match(Path.GetFileName(folder));
            if (!m.Success) continue;
            if (string.Equals(m.Groups[2].Value, weldLabel, StringComparison.OrdinalIgnoreCase)) return folder;
            if (int.TryParse(m.Groups[1].Value, out var number) && number > highest) highest = number;
        }
        var made = Path.Combine(isoFolderOnCard, $"{highest + 1:000}_{FolderConventions.SanitizeFolderName(weldLabel)}");
        Directory.CreateDirectory(made);
        return made;
    }

    private static string FreeName(string path)
    {
        if (!File.Exists(path)) return path;
        var folder = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(folder, $"{name}-{i}{extension}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    public static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20,
                                                FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
    }
}
