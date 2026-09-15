namespace IMPWeldPhotos;

/// <summary>
/// 140_Zvari\{project}\{unit}\{BomCode} - {BomName}. Each level is found by the code
/// in its desktop.ini (FolderConventions), so a renamed project, unit or isometrija
/// renames its folder instead of getting a second one.
/// </summary>
public sealed class DestinationTree
{
    public const string UnresolvedFolderName = "_Nerazvrsceno";

    private readonly string _root;
    private readonly Dictionary<string, Dictionary<string, string>> _indexes = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _iniWritten = new(StringComparer.OrdinalIgnoreCase);

    public DestinationTree(string root) => _root = root;

    public string PlannedIsoPath(BomInfo b) => Path.Combine(
        _root,
        FolderConventions.SanitizeFolderName(b.ProjectFolderName),
        FolderConventions.SanitizeFolderName(b.UnitFolderName),
        FolderConventions.SanitizeFolderName(b.IsoFolderName));

    /// <summary>The card carries no unit, so parked photos are filed by BOM code alone.</summary>
    public string UnresolvedIsoFolder(int bomCode) =>
        Path.Combine(_root, UnresolvedFolderName, bomCode.ToString());

    /// <summary>The isometrija folder as it exists now, or null. For the preview:
    /// creates and renames nothing.</summary>
    public string? FindIsoFolder(BomInfo b, ICollection<string> warnings)
    {
        var project = Find(_root, b.ProjectCode, b.ProjectFolderName, "projekta", warnings);
        var unit = project == null ? null : Find(project, b.UnitCode, b.UnitFolderName, "sklopa", warnings);
        return unit == null ? null : Find(unit, b.BomCode.ToString(), b.IsoFolderName, "izometrije", warnings);
    }

    /// <summary>Creates or renames each level as GENERATE_FOLDERS does and writes its
    /// desktop.ini. Throws when a folder can't be created at all.</summary>
    public string EnsureIsoFolder(BomInfo b, ICollection<string> notes)
    {
        var project = Ensure(_root, b.ProjectCode, b.ProjectFolderName, "projekta", notes);
        var unit = Ensure(project, b.UnitCode, b.UnitFolderName, "sklopa", notes);
        return Ensure(unit, b.BomCode.ToString(), b.IsoFolderName, "izometrije", notes);
    }

    private string? Find(string parent, string code, string desiredName, string label, ICollection<string> warnings)
    {
        if (Index(parent, label, warnings).TryGetValue(code, out var path)) return path;
        var desired = Path.Combine(parent, FolderConventions.SanitizeFolderName(desiredName));
        return Directory.Exists(desired) ? desired : null;
    }

    private string Ensure(string parent, string code, string desiredName, string label, ICollection<string> notes)
    {
        var index = Index(parent, label, notes);
        var action = FolderConventions.EnsureFolder(parent, code, desiredName, index, out var path, out var detail);
        switch (action)
        {
            case FolderAction.Renamed:
                notes.Add($"Preimenovana mapa {label} {code}: {detail}");
                break;
            case FolderAction.Failed when path == null:
                throw new IOException($"mape {label} {code} ni mogoče ustvariti: {detail}");
            case FolderAction.Failed:
                notes.Add($"Mape {label} {code} ni mogoče preimenovati ({detail}); uporabljena je obstoječa.");
                break;
        }
        if (_iniWritten.Add(path!)) FolderConventions.WriteDesktopIni(path!, code);
        return path!;
    }

    private Dictionary<string, string> Index(string parent, string label, ICollection<string> warnings)
    {
        if (!_indexes.TryGetValue(parent, out var index))
            _indexes[parent] = index = FolderConventions.IndexFoldersByCode(parent, label, warnings);
        return index;
    }
}
