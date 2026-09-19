using System.Text.Json;

namespace IMPWeldPhotos;

/// <summary>Where one isometrija belongs. Folder names are the names the folders
/// should have; codes are what the folders are found by. FromDatabase marks data that
/// may rename existing folders; titles.json data never does.</summary>
public sealed record BomInfo(
    int BomCode,
    string BomName,
    string ProjectCode,
    string ProjectFolderName,
    string UnitCode,
    string UnitFolderName,
    string Source,
    bool FromDatabase)
{
    public string IsoFolderName =>
        string.IsNullOrWhiteSpace(BomName) ? BomCode.ToString() : $"{BomCode} - {BomName}";
}

/// <summary>Info when resolved; otherwise Reason says why not.</summary>
public sealed record BomResolution(BomInfo? Info, string? Reason);

/// <summary>
/// BomCode -> project, unit and BOM name. The CommonData database first (needs the user's
/// sign-in); titles.json as the offline fallback. Make one per scan or import: titles.json
/// is re-read in the constructor because IMPIsoIndexer keeps regenerating it.
/// </summary>
public sealed class BomResolver
{
    private readonly AppConfig _cfg;
    private readonly CommonDataApi? _api;
    private readonly Dictionary<string, string> _titles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, BomResolution> _cache = new();
    private readonly Dictionary<int, List<BomIsoRow>> _dbRows = new();

    /// <summary>Why the database couldn't be asked at all (not signed in, offline, no rights).</summary>
    private string? _dbProblem;

    public string? TitlesProblem { get; }

    /// <summary>What the user should know about where the data came from; shown with the preview.</summary>
    public NoteList Notes { get; } = new();

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
            TitlesProblem = $"titles.json ni berljiv ({ex.Message}).";
        }
    }

    /// <summary>Asks the database for many BOM codes in a few requests, so a card with 40
    /// isometrije doesn't cost 40 round trips.</summary>
    public async Task PrefetchAsync(IEnumerable<int> bomCodes, CancellationToken ct)
    {
        var todo = bomCodes.Distinct().Where(c => !_dbRows.ContainsKey(c)).ToList();
        if (todo.Count == 0 || !CanQuery()) return;
        try
        {
            var rows = await _api!.GetBomIsoRowsAsync(todo, ct);
            foreach (var code in todo) _dbRows[code] = rows.Where(r => r.BomCode == code).ToList();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (NotSignedInException ex)
        {
            _dbProblem = ex.Message;
        }
        catch (Exception ex)
        {
            _dbProblem = _api!.DisabledReason ?? $"CommonData ni dosegljiv ({CommonDataApi.Describe(ex)})";
        }
    }

    private bool CanQuery()
    {
        if (_dbProblem != null) return false; // one failure per resolver, not one per BOM
        _dbProblem = _api == null ? "povezava s CommonData je izklopljena"
            : _api.DisabledReason is { } off ? off
            : !_api.IsSignedIn ? "niste prijavljeni v CommonData"
            : null;
        return _dbProblem == null;
    }

    public async Task<BomResolution> ResolveAsync(int bomCode, CancellationToken ct)
    {
        if (_cache.TryGetValue(bomCode, out var hit)) return hit;
        await PrefetchAsync(new[] { bomCode }, ct);

        string? dbReason = null;
        if (_dbRows.TryGetValue(bomCode, out var rows))
        {
            var fromDb = FromDatabase(bomCode, rows, out dbReason);
            if (fromDb != null) return _cache[bomCode] = new BomResolution(fromDb, null);
        }

        var whyNotDb = dbReason ?? _dbProblem ?? "ni v bazi";
        var fromTitles = FromTitles(bomCode, out var titlesReason);
        if (fromTitles != null)
        {
            Notes.Add(dbReason != null
                ? $"Izometrija {bomCode}: {dbReason}; uporabljen je titles.json."
                : $"Podatki o izometrijah so iz titles.json, ker {whyNotDb}. Obstoječe mape se zato ne preimenujejo.");
            return _cache[bomCode] = new BomResolution(fromTitles, null);
        }
        return _cache[bomCode] = new BomResolution(null, $"{whyNotDb}; {titlesReason}");
    }

    private static BomInfo? FromDatabase(int bomCode, List<BomIsoRow> rows, out string? reason)
    {
        reason = null;
        if (rows.Count == 0)
        {
            reason = "ni v bazi CommonData";
            return null;
        }
        // FabBomIsoView is keyed by BomCode + UnitCode, so one BOM can come back once per
        // unit. The card doesn't say which unit, so more than one is left unresolved.
        var units = rows.Select(r => (r.ProjectCode, r.UnitCode)).Distinct().Count();
        if (units > 1)
        {
            reason = $"v bazi spada v {units} sklope, ni jasno v katerega";
            return null;
        }
        var row = rows[0];
        if (string.IsNullOrWhiteSpace(row.ProjectCode) || row.UnitCode == null)
        {
            reason = "v bazi nima projekta ali sklopa";
            return null;
        }

        var unitFolder = string.IsNullOrWhiteSpace(row.UnitName) ? row.UnitCode.Value.ToString() : row.UnitName.Trim();
        return new BomInfo(
            bomCode,
            row.BomName?.Trim() ?? "",
            row.ProjectCode.Trim(),
            FolderConventions.BuildProjectFolderName(row.ProjectCode, row.ProjectName?.Trim()),
            row.UnitCode.Value.ToString(),
            unitFolder,
            "CommonData",
            FromDatabase: true);
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
                           projectCode, segs[0], unitCode, segs[1], "titles.json", FromDatabase: false);
    }
}
