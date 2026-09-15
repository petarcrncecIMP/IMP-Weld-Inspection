using System.Text.RegularExpressions;

namespace IMPWeldPhotos;

public sealed record WeldFolder(
    int BomCode,
    string WeldLabel,
    string Path,
    IReadOnlyList<FileInfo> Files);

/// <summary>Welds lists only weld folders holding photos; empty ones are counted.</summary>
public sealed record CardScan(
    string Root,
    IReadOnlyList<string> BomFolders,
    IReadOnlyList<WeldFolder> Welds,
    int EmptyWeldFolders)
{
    public bool IsCard => BomFolders.Count > 0;
}

/// <summary>
/// Reads the card: isometrija folders straight on its root, each holding weld folders,
/// {BomCode}\{NNN}_{WeldLabel}\photos. Nothing deeper than that is ever listed, so a
/// large card costs one listing per isometrija.
/// </summary>
public static class CardScanner
{
    private static readonly Regex BomRx = new(@"^\d{7}$");
    private static readonly Regex WeldRx = new(@"^\d{3}_(F?W\d+(\.\d)?[a-z]?)$");

    public static CardScan Scan(string root)
    {
        var boms = new List<string>();
        var welds = new List<WeldFolder>();
        var empty = 0;

        foreach (var bom in Subdirectories(root).Where(b => BomRx.IsMatch(Path.GetFileName(b))))
        {
            var bomCode = int.Parse(Path.GetFileName(bom));
            var found = false;
            foreach (var weld in Subdirectories(bom))
            {
                var m = WeldRx.Match(Path.GetFileName(weld));
                if (!m.Success) continue;
                found = true;
                var files = Files(weld).Where(MediaFiles.IsImportable)
                                       .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                                       .ToList();
                if (files.Count == 0)
                {
                    empty++;
                    continue;
                }
                welds.Add(new WeldFolder(bomCode, m.Groups[1].Value, weld, files));
            }
            // A 7-digit folder with no weld folders inside isn't ours.
            if (found) boms.Add(bom);
        }
        return new CardScan(root, boms, welds, empty);
    }

    private static string[] Subdirectories(string dir)
    {
        try
        {
            return new DirectoryInfo(dir).GetDirectories()
                .Where(d => (d.Attributes & FileAttributes.Hidden) == 0)
                .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .Select(d => d.FullName)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    private static FileInfo[] Files(string dir)
    {
        try
        {
            return new DirectoryInfo(dir).GetFiles();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<FileInfo>();
        }
    }
}
