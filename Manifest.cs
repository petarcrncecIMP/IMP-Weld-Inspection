using System.Text.Json;

namespace IMPWeldPhotos;

/// <summary>
/// Hidden .imported.json per isometrija folder: destination file name -> SHA-256 of
/// the source file it was made from. The stamped copy has different bytes from the
/// source, so this is the only way to tell that a card photo is already in.
/// </summary>
public sealed class Manifest
{
    public const string FileName = ".imported.json";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly string _path;

    public Dictionary<string, string> Files { get; }

    private Manifest(string path, Dictionary<string, string> files)
    {
        _path = path;
        Files = files;
    }

    /// <summary>A missing file is an empty manifest. An unreadable one throws:
    /// carrying on with an empty one would re-import every photo already there.</summary>
    public static Manifest Load(string folder)
    {
        var path = Path.Combine(folder, FileName);
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(path))
        {
            var read = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))
                       ?? throw new InvalidDataException("prazen ali neveljaven JSON");
            foreach (var kv in read) files[kv.Key] = kv.Value;
        }
        return new Manifest(path, files);
    }

    /// <summary>Forgets photos that are no longer in the folder, so a photo someone
    /// deleted is imported again instead of being skipped as a duplicate. True when
    /// anything was dropped.</summary>
    public bool DropMissing()
    {
        var folder = Path.GetDirectoryName(_path)!;
        var gone = Files.Keys.Where(name => !File.Exists(Path.Combine(folder, name))).ToList();
        foreach (var name in gone) Files.Remove(name);
        return gone.Count > 0;
    }

    /// <summary>Written to a temp name and moved into place, so a reader never sees
    /// half a file.</summary>
    public void Save()
    {
        var tmp = _path + ".tmp";
        if (File.Exists(tmp))
        {
            File.SetAttributes(tmp, FileAttributes.Normal);
            File.Delete(tmp);
        }
        var sorted = new SortedDictionary<string, string>(Files, StringComparer.OrdinalIgnoreCase);
        File.WriteAllText(tmp, JsonSerializer.Serialize(sorted, Json));
        if (File.Exists(_path)) File.SetAttributes(_path, FileAttributes.Normal);
        File.Move(tmp, _path, overwrite: true);
        try
        {
            File.SetAttributes(_path, FileAttributes.Hidden);
        }
        catch
        {
            // The content is what matters; a visible manifest still works.
        }
    }

    public void Delete()
    {
        if (!File.Exists(_path)) return;
        File.SetAttributes(_path, FileAttributes.Normal);
        File.Delete(_path);
    }
}
