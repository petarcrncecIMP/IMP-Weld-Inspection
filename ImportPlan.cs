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
/// comes from the photos already in the destination folder, counted by name; exact
/// duplicates are found by hash at import.
/// </summary>
public static class ImportPlanner
{
    public static async Task<ImportPlan> BuildAsync(CardScan scan, AppConfig cfg, BomResolver resolver, CancellationToken ct)
    {
        var warnings = new NoteList();
        if (resolver.TitlesProblem is { } titles) warnings.Add(titles);
        var tree = new DestinationTree(cfg.DestinationRoot);
        var plans = new List<WeldPlan>();

        await resolver.PrefetchAsync(scan.Welds.Select(w => w.BomCode), ct);
        foreach (var bomGroup in scan.Welds.GroupBy(w => w.BomCode).OrderBy(g => g.Key))
        {
            var res = await resolver.ResolveAsync(bomGroup.Key, ct);

            string destination;
            string? existing;
            if (res.Info is { } info)
            {
                existing = tree.FindIsoFolder(info, warnings);
                destination = tree.ExpectedIsoPath(info, warnings);
            }
            else
            {
                warnings.Add($"Izometrija {bomGroup.Key} je nerazvrščena: {res.Reason}.");
                destination = tree.UnresolvedIsoFolder(bomGroup.Key);
                existing = Directory.Exists(destination) ? destination : null;
            }

            // One isometrija folder holds each weld label once, but grouping keeps two
            // folders that differ only in the NNN prefix together as one weld.
            foreach (var weldGroup in bomGroup.GroupBy(w => w.WeldLabel, StringComparer.Ordinal))
            {
                var files = weldGroup.SelectMany(w => w.Files).ToList();
                var already = existing == null ? 0 : Numbering.CountImported(existing, WeldPlan.PrefixFor(bomGroup.Key, weldGroup.Key));
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

        // Where the data came from goes first: it explains everything below it.
        var all = new NoteList();
        foreach (var note in resolver.Notes) all.Add(note);
        foreach (var w in warnings) all.Add(w);
        return new ImportPlan
        {
            Scan = scan,
            Welds = plans.OrderBy(p => p.BomCode).ThenBy(p => p.SortKey, StringComparer.Ordinal).ToList(),
            Warnings = all.ToList(),
        };
    }
}

/// <summary>The -{n} in {BomCode}-{WeldLabel}-{n}.{ext}, read from the files on disk.</summary>
public static class Numbering
{
    /// <summary>Highest n already used in the folder, leftover .part files included. The
    /// next photo gets one more, so nothing is overwritten.</summary>
    public static int Highest(string folder, string prefix)
    {
        var rx = new Regex("^" + Regex.Escape(prefix) + @"-(\d+)\.", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var max = 0;
        foreach (var name in Names(folder))
        {
            var m = rx.Match(name);
            if (m.Success && int.TryParse(m.Groups[1].Value, out var n) && n > max) max = n;
        }
        return max;
    }

    /// <summary>The finished photos of one weld in the folder (no .part files).</summary>
    public static List<string> WeldFiles(string folder, string prefix)
    {
        var rx = new Regex("^" + Regex.Escape(prefix) + @"-\d+\.[^.]+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return Names(folder).Where(n => rx.IsMatch(n)).Select(n => Path.Combine(folder, n)).ToList();
    }

    public static int CountImported(string folder, string prefix) => WeldFiles(folder, prefix).Count;

    private static IEnumerable<string> Names(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder).Select(Path.GetFileName).OfType<string>().ToList();
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }
}
