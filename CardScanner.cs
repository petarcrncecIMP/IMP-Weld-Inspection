using System.Text.RegularExpressions;

namespace IMPWeldPhotos;

public sealed record WeldFolder(
    int UnitCode,
    string UnitName,
    int BomCode,
    string WeldLabel,
    string Path,
    IReadOnlyList<FileInfo> Files);

/// <summary>Welds lists only weld folders holding photos; empty ones are counted.</summary>
public sealed record CardScan(
    string Root,
    IReadOnlyList<string> UnitFolders,
    IReadOnlyList<WeldFolder> Welds,
    int EmptyWeldFolders)
{
    public bool IsCard => UnitFolders.Count > 0;
}

/// <summary>
/// Reads the tree Kosovnice's "Prenesi prazno strukturo map" puts on the card
/// (IMP_Kosovnice/src/folderStructure.ts): {UnitCode}-{UnitName}\{BomCode}\{NNN}_{WeldLabel}.
/// Depth-limited on purpose: a card is never walked in full.
/// </summary>
public static class CardScanner
{
    private static readonly Regex UnitRx = new(@"^(\d+)-(.+)$");
    private static readonly Regex BomRx = new(@"^\d{7}$");
    private static readonly Regex WeldRx = new(@"^\d{3}_(F?W\d+(\.\d)?[a-z]?)$");

    /// <summary>Unit folders directly under the root, or under a subfolder up to 2
    /// levels deep. The root itself counts too, for a folder picked by hand.</summary>
    public static List<string> FindUnitFolders(string root)
    {
        var found = new List<string>();
        Visit(root, 0, found);
        return found;
    }

    private static void Visit(string dir, int depth, List<string> found)
    {
        if (IsUnitFolder(dir))
        {
            found.Add(dir);
            return;
        }
        if (depth >= 3) return;
        foreach (var sub in Subdirectories(dir)) Visit(sub, depth + 1, found);
    }

    private static bool IsUnitFolder(string dir) =>
        UnitRx.IsMatch(Path.GetFileName(dir)) &&
        Subdirectories(dir).Any(bom => BomRx.IsMatch(Path.GetFileName(bom)) &&
                                       Subdirectories(bom).Any(weld => WeldRx.IsMatch(Path.GetFileName(weld))));

    public static CardScan Scan(string root)
    {
        var units = FindUnitFolders(root);
        var welds = new List<WeldFolder>();
        var empty = 0;
        foreach (var unit in units)
        {
            var m = UnitRx.Match(Path.GetFileName(unit));
            if (!int.TryParse(m.Groups[1].Value, out var unitCode)) continue;
            var unitName = m.Groups[2].Value;

            foreach (var bom in Subdirectories(unit).Where(b => BomRx.IsMatch(Path.GetFileName(b))))
            {
                var bomCode = int.Parse(Path.GetFileName(bom));
                foreach (var weld in Subdirectories(bom))
                {
                    var wm = WeldRx.Match(Path.GetFileName(weld));
                    if (!wm.Success) continue;
                    var files = Files(weld).Where(MediaFiles.IsImportable)
                                           .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                                           .ToList();
                    if (files.Count == 0)
                    {
                        empty++;
                        continue;
                    }
                    welds.Add(new WeldFolder(unitCode, unitName, bomCode, wm.Groups[1].Value, weld, files));
                }
            }
        }
        return new CardScan(root, units, welds, empty);
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
