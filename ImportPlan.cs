using System.Collections.ObjectModel;
using System.Text.RegularExpressions;

namespace IMPWeldPhotos;

public enum WeldStatus
{
    New,
    AlreadyImported,
    PartlyNew,
    Unresolved,
    Failed,
}

/// <summary>One row of the preview: every photo on the card for one weld.</summary>
public sealed class WeldPlan
{
    public required int BomCode { get; init; }
    public required string WeldLabel { get; init; }
    public required string SortKey { get; init; }
    public required IReadOnlyList<FileInfo> Files { get; init; }
    public required BomResolution Resolution { get; init; }
    public required string DestinationFolder { get; init; }
    public int AlreadyImported { get; init; }
    public WeldStatus Status { get; set; }
    public string? StatusTip { get; set; }

    public static string PrefixFor(int bomCode, string weldLabel) =>
        $"{bomCode}-{FolderConventions.SanitizeFolderName(weldLabel)}";

    public string FilePrefix => PrefixFor(BomCode, WeldLabel);
    public string Unit => Resolution.Info?.UnitFolderName ?? "—";
    public string Isometrija => Resolution.Info?.IsoFolderName ?? BomCode.ToString();
    public int Photos => Files.Count;
    public long Bytes => Files.Sum(f => f.Length);

    public string StatusText => Status switch
    {
        WeldStatus.New => "nova",
        WeldStatus.AlreadyImported => "že uvožena",
        WeldStatus.PartlyNew => "delno nova",
        WeldStatus.Unresolved => "nerazvrščena",
        _ => "napaka",
    };
}

public sealed class ImportPlan
{
    public required CardScan Scan { get; init; }
    public required IReadOnlyList<WeldPlan> Welds { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
    public int PhotoCount => Welds.Sum(w => w.Photos);
}

/// <summary>A list that ignores a line it already holds, so a warning met once per
/// weld is still shown once.</summary>
public sealed class NoteList : Collection<string>
{
    private readonly HashSet<string> _seen = new();

    protected override void InsertItem(int index, string item)
    {
        if (_seen.Add(item)) base.InsertItem(index, item);
    }
}

/// <summary>
/// Builds the preview. Read-only: nothing is created, renamed or hashed here. Status
/// comes from the manifest's file names; exact source hashes are checked at import.
/// </summary>
public static class ImportPlanner
{
    public static async Task<ImportPlan> BuildAsync(CardScan scan, AppConfig cfg, BomResolver resolver, CancellationToken ct)
    {
        var warnings = new NoteList();
        if (resolver.TitlesProblem is { } titles) warnings.Add(titles);
        var tree = new DestinationTree(cfg.DestinationRoot);
        var plans = new List<WeldPlan>();

        foreach (var bomGroup in scan.Welds.GroupBy(w => w.BomCode).OrderBy(g => g.Key))
        {
            var res = await resolver.ResolveAsync(bomGroup.Key, ct);

            string destination;
            string? existing;
            if (res.Info is { } info)
            {
                existing = tree.FindIsoFolder(info, warnings);
                destination = tree.PlannedIsoPath(info);
            }
            else
            {
                warnings.Add($"Izometrija {bomGroup.Key} je nerazvrščena: {res.Reason}.");
                destination = tree.UnresolvedIsoFolder(bomGroup.Key);
                existing = Directory.Exists(destination) ? destination : null;
            }

            Manifest? manifest = null;
            if (existing != null)
            {
                try
                {
                    manifest = Manifest.Load(existing);
                    manifest.DropMissing(); // preview only: not saved
                }
                catch (Exception ex)
                {
                    warnings.Add($"{Path.Combine(existing, Manifest.FileName)} ni berljiv: {ex.Message}");
                }
            }

            // One isometrija folder holds each weld label once, but grouping keeps two
            // folders that differ only in the NNN prefix together as one weld.
            foreach (var weldGroup in bomGroup.GroupBy(w => w.WeldLabel, StringComparer.Ordinal))
            {
                var files = weldGroup.SelectMany(w => w.Files).ToList();
                var already = Numbering.CountImported(manifest, WeldPlan.PrefixFor(bomGroup.Key, weldGroup.Key));
                var plan = new WeldPlan
                {
                    BomCode = bomGroup.Key,
                    WeldLabel = weldGroup.Key,
                    SortKey = Path.GetFileName(weldGroup.First().Path),
                    Files = files,
                    Resolution = res,
                    DestinationFolder = destination,
                    AlreadyImported = already,
                };
                plan.Status = res.Info == null ? WeldStatus.Unresolved
                    : already >= files.Count ? WeldStatus.AlreadyImported
                    : already > 0 ? WeldStatus.PartlyNew
                    : WeldStatus.New;
                plan.StatusTip = res.Info == null ? res.Reason
                    : already > 0 ? $"V cilju je že {already} fotografij tega zvara." : null;
                plans.Add(plan);
            }
        }

        return new ImportPlan
        {
            Scan = scan,
            Welds = plans.OrderBy(p => p.BomCode).ThenBy(p => p.SortKey, StringComparer.Ordinal).ToList(),
            Warnings = warnings.ToList(),
        };
    }

}

/// <summary>The -{n} in {BomCode}-{WeldLabel}-{n}.{ext}.</summary>
public static class Numbering
{
    /// <summary>Highest n already used, on disk (including leftover .part files) or
    /// in the manifest. The next photo gets one more, so nothing is overwritten and
    /// numbering never restarts.</summary>
    public static int Highest(string folder, Manifest? manifest, string prefix)
    {
        var rx = new Regex("^" + Regex.Escape(prefix) + @"-(\d+)\.", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var names = manifest?.Files.Keys.ToList() ?? new List<string>();
        try
        {
            names.AddRange(Directory.EnumerateFiles(folder).Select(Path.GetFileName)!);
        }
        catch (DirectoryNotFoundException)
        {
        }
        var max = 0;
        foreach (var name in names)
        {
            var m = rx.Match(name);
            if (m.Success && int.TryParse(m.Groups[1].Value, out var n) && n > max) max = n;
        }
        return max;
    }

    public static int CountImported(Manifest? manifest, string prefix)
    {
        if (manifest == null) return 0;
        var rx = new Regex("^" + Regex.Escape(prefix) + @"-\d+\.[^.]+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return manifest.Files.Keys.Count(k => rx.IsMatch(k));
    }
}
