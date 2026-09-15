using System.Text.Json;

namespace IMPWeldPhotos;

/// <summary>Where one isometrija belongs. Folder names are the names the folders
/// should have; codes are what the folders are found by.</summary>
public sealed record BomInfo(
    int BomCode,
    string BomName,
    string ProjectCode,
    string ProjectFolderName,
    string UnitCode,
    string UnitFolderName,
    string Source)
{
    public string IsoFolderName =>
        string.IsNullOrWhiteSpace(BomName) ? BomCode.ToString() : $"{BomCode} - {BomName}";
}

/// <summary>Info when resolved; otherwise Reason says why not.</summary>
public sealed record BomResolution(BomInfo? Info, string? Reason);

/// <summary>
/// BomCode -> project, unit and BOM name. titles.json first (offline, no sign-in),
/// then the CommonData API. Make one per scan or import: titles.json is re-read in
/// the constructor because IMPIsoIndexer keeps regenerating it.
/// </summary>
public sealed class BomResolver
{
    private readonly AppConfig _cfg;
    private readonly CommonDataApi? _api;
    private readonly Dictionary<string, string> _titles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, BomResolution> _cache = new();

    public string? TitlesProblem { get; }

    public BomResolver(AppConfig cfg, CommonDataApi? api)
    {
        _cfg = cfg;
        _api = api;
        try
        {
            // Entries: BomCode -> { Path relative to IsoRoot }. "Root" is the server's
            // own path and is ignored.
            using var doc = JsonDocument.Parse(File.ReadAllText(cfg.TitlesPath));
            if (doc.RootElement.TryGetProperty("Entries", out var entries) && entries.ValueKind == JsonValueKind.Object)
            {
                foreach (var e in entries.EnumerateObject())
                {
                    if (e.Value.ValueKind == JsonValueKind.Object &&
                        e.Value.TryGetProperty("Path", out var p) &&
                        p.GetString() is { Length: > 0 } path)
                        _titles[e.Name.Trim()] = path;
                }
            }
        }
        catch (Exception ex)
        {
            TitlesProblem = $"titles.json ni berljiv ({ex.Message}); izometrije se iščejo samo prek API-ja.";
        }
    }

    public async Task<BomResolution> ResolveAsync(int bomCode, CancellationToken ct)
    {
        if (_cache.TryGetValue(bomCode, out var hit)) return hit;
        var info = FromTitles(bomCode, out var titlesReason);
        var res = info != null
            ? new BomResolution(info, null)
            : await FromApiAsync(bomCode, titlesReason, ct);
        _cache[bomCode] = res;
        return res;
    }

    private BomInfo? FromTitles(int bomCode, out string reason)
    {
        reason = "ni v titles.json";
        if (!_titles.TryGetValue(bomCode.ToString(), out var rel)) return null;

        var segs = rel.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        if (segs.Length < 3)
        {
            reason = $"nepričakovana pot v titles.json: {rel}";
            return null;
        }

        // Project and unit are the first two segments; their codes come from the
        // desktop.ini in those folders, never from the names.
        var projectDir = Path.Combine(_cfg.IsoRoot, segs[0]);
        var unitDir = Path.Combine(projectDir, segs[1]);
        var projectCode = FolderConventions.ReadFolderCode(Path.Combine(projectDir, FolderConventions.DesktopIni));
        if (projectCode == "")
        {
            reason = $"mapa projekta '{segs[0]}' v 130_Izometrije nima kode v desktop.ini";
            return null;
        }
        var unitCode = FolderConventions.ReadFolderCode(Path.Combine(unitDir, FolderConventions.DesktopIni));
        if (unitCode == "")
        {
            reason = $"mapa sklopa '{segs[0]}\\{segs[1]}' v 130_Izometrije nima kode v desktop.ini";
            return null;
        }

        return new BomInfo(bomCode, Path.GetFileNameWithoutExtension(segs[^1]),
                           projectCode, segs[0], unitCode, segs[1], "titles.json");
    }

    private async Task<BomResolution> FromApiAsync(int bomCode, string titlesReason, CancellationToken ct)
    {
        if (_api == null) return new(null, $"{titlesReason}; CommonData API ni nastavljen");
        if (_api.DisabledReason is { } off) return new(null, $"{titlesReason}; {off}");

        List<BomIsoRow> rows;
        try
        {
            rows = await _api.GetBomIsoRowsAsync(bomCode, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new(null, $"{titlesReason}; poizvedba v CommonData API ni uspela: {CommonDataApi.Describe(ex)}");
        }

        if (rows.Count == 0) return new(null, $"{titlesReason}; tudi v bazi je ni");

        // FabBomIsoView is keyed by BomCode + UnitCode, so one BOM can come back once per
        // unit. The card doesn't say which unit, so more than one is left unresolved.
        var units = rows.Select(r => (r.ProjectCode, r.UnitCode)).Distinct().Count();
        if (units > 1) return new(null, $"{titlesReason}; v bazi spada v {units} sklope, ni jasno v katerega");
        var row = rows[0];
        if (string.IsNullOrWhiteSpace(row.ProjectCode) || row.UnitCode == null)
            return new(null, $"{titlesReason}; v bazi nima projekta ali sklopa");

        var unitFolder = string.IsNullOrWhiteSpace(row.UnitName) ? row.UnitCode.Value.ToString() : row.UnitName.Trim();
        return new(new BomInfo(
            bomCode,
            row.BomName?.Trim() ?? "",
            row.ProjectCode.Trim(),
            FolderConventions.BuildProjectFolderName(row.ProjectCode, row.ProjectName?.Trim()),
            row.UnitCode.Value.ToString(),
            unitFolder,
            "CommonData API"), null);
    }
}
