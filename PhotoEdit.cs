using System.Text.RegularExpressions;

namespace IMPWeldPhotos;

/// <summary>
/// Putting right what an import got wrong: a photo filed under the wrong weld, or one that
/// shouldn't be on the share at all. Both work inside the isometrija folder, where all of that
/// isometrija's photos live, so a move is a rename into the other weld's numbering.
///
/// A deleted photo comes back on the next import while it is still on the card; a moved one
/// doesn't, because the import compares hashes against the whole isometrija folder.
/// </summary>
public static class PhotoEdit
{
    private static readonly Regex StoredName = new(@"^(\d{7})-(.+)-(\d+)\.[^.]+$", RegexOptions.CultureInvariant);

    /// <summary>Renames the photo into another weld of the same isometrija, with that weld's
    /// next free number. Returns the new path (the old one when it is already there).</summary>
    public static async Task<string> MoveToWeldAsync(string path, string weldLabel, CancellationToken ct)
    {
        var folder = Path.GetDirectoryName(path) ?? throw new IOException($"{path} ni v mapi");
        var name = Path.GetFileName(path);
        var parsed = StoredName.Match(name);
        if (!parsed.Success) throw new IOException($"{name} ni ime uvožene fotografije");

        var label = FolderConventions.SanitizeFolderName(weldLabel.Trim());
        if (label.Length == 0) throw new IOException("oznaka zvara je prazna");
        if (string.Equals(label, parsed.Groups[2].Value, StringComparison.OrdinalIgnoreCase)) return path;

        var prefix = $"{parsed.Groups[1].Value}-{label}";
        var target = Path.Combine(folder, $"{prefix}-{Numbering.Highest(folder, prefix) + 1}{Path.GetExtension(name).ToLowerInvariant()}");
        await ShareRetry.RunAsync(() => File.Move(path, target), ct);
        return target;
    }

    public static async Task DeleteAsync(string path, CancellationToken ct) =>
        await ShareRetry.RunAsync(() =>
        {
            if (!File.Exists(path)) return;
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
        }, ct);
}
