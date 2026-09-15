using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;

namespace IMPWeldPhotos;

/// <summary>One isometrija folder on the share, as listed in the viewer.</summary>
public sealed class IsoEntry
{
    private static readonly Regex FolderRx = new(@"^(\d{7})(?: - (.*))?$");

    public required string Path { get; init; }
    public required string FolderName { get; init; }
    public required string ProjectName { get; init; }
    public required string UnitName { get; init; }
    public required string BomCode { get; init; }
    public required string Title { get; init; }
    public required string SearchText { get; init; }

    public static IsoEntry Create(string path, string projectName, string unitName)
    {
        var folder = System.IO.Path.GetFileName(path);
        var m = FolderRx.Match(folder);
        return new IsoEntry
        {
            Path = path,
            FolderName = folder,
            ProjectName = projectName,
            UnitName = unitName,
            BomCode = m.Success ? m.Groups[1].Value : folder,
            Title = m.Success ? m.Groups[2].Value : "",
            SearchText = $"{folder} {unitName} {projectName}".ToLowerInvariant(),
        };
    }
}

/// <summary>A project folder in 140_Zvari, as offered by the project picker.</summary>
public sealed class ProjectEntry
{
    private static readonly Regex FolderRx = new(@"^(\d[\d-]*\d) - (.+)$", RegexOptions.CultureInvariant);
    private static readonly Regex DashesAndSpaces = new(@"[-\s]", RegexOptions.CultureInvariant);

    /// <summary>The folder name: the key, and what IsoEntry.ProjectName holds.</summary>
    public required string FolderName { get; init; }

    /// <summary>Formatted code, e.g. 3-6-40-00-4501; empty for Nerazvrščeno.</summary>
    public required string Code { get; init; }

    public required string Name { get; init; }
    public required int IsoCount { get; init; }

    /// <summary>The selected project, shown bold in the list.</summary>
    public bool IsCurrent { get; set; }

    public static ProjectEntry Create(string folderName, int isoCount)
    {
        var m = FolderRx.Match(folderName);
        return new ProjectEntry
        {
            FolderName = folderName,
            Code = m.Success ? m.Groups[1].Value : "",
            Name = m.Success ? m.Groups[2].Value : folderName,
            IsoCount = isoCount,
        };
    }

    /// <summary>One entry per project, in the order the isometrije are listed.</summary>
    public static List<ProjectEntry> FromIsos(IEnumerable<IsoEntry> isos) =>
        isos.GroupBy(i => i.ProjectName, StringComparer.OrdinalIgnoreCase)
            .Select(g => Create(g.Key, g.Count()))
            .ToList();

    /// <summary>Kosovnice's rule: case-insensitive, and a match if the code without dashes and
    /// spaces contains the query without them (364000 finds 3-6-40-00-…), or the code contains
    /// the query, or the name does.</summary>
    public bool Matches(string? query)
    {
        var q = (query ?? "").Trim().ToLowerInvariant();
        if (q.Length == 0) return true;
        var code = Code.ToLowerInvariant();
        return DashesAndSpaces.Replace(code, "").Contains(DashesAndSpaces.Replace(q, ""))
               || code.Contains(q)
               || Name.ToLowerInvariant().Contains(q);
    }
}

/// <summary>Lists 140_Zvari: project, unit, isometrija, plus _Nerazvrsceno\{BomCode}.
/// Three directory levels only; files are never listed here.</summary>
public static class ServerIndex
{
    public const string UnresolvedProject = "Nerazvrščeno";

    public static (List<IsoEntry> Isos, string? Problem) Load(string root)
    {
        var list = new List<IsoEntry>();
        if (!Directory.Exists(root)) return (list, $"Mapa {root} ni dosegljiva.");

        foreach (var project in Dirs(root))
        {
            var projectName = Path.GetFileName(project);
            if (projectName.Equals(DestinationTree.UnresolvedFolderName, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var iso in Dirs(project)) list.Add(IsoEntry.Create(iso, UnresolvedProject, "brez sklopa"));
                continue;
            }
            foreach (var unit in Dirs(project))
                foreach (var iso in Dirs(unit))
                    list.Add(IsoEntry.Create(iso, projectName, Path.GetFileName(unit)));
        }

        var ordered = list
            .OrderBy(i => i.ProjectName == UnresolvedProject ? 1 : 0)
            .ThenBy(i => i.ProjectName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.UnitName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.FolderName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return (ordered, null);
    }

    private static string[] Dirs(string dir)
    {
        try
        {
            return new DirectoryInfo(dir).GetDirectories()
                .Where(d => (d.Attributes & FileAttributes.Hidden) == 0)
                .Select(d => d.FullName)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }
}

/// <summary>One photo or video in an isometrija folder.</summary>
public sealed class PhotoItem : INotifyPropertyChanged
{
    private static readonly Regex NameRx = new(@"^(\d{7})-(.+)-(\d+)(\.[^.]+)$", RegexOptions.CultureInvariant);
    private static readonly Regex WeldRx = new(@"^(F?)W(\d+(?:\.\d+)?)([a-z]?)$", RegexOptions.CultureInvariant);
    public const string OtherLabel = "Ostalo";

    private ImageSource? _thumbnail;

    public required string Path { get; init; }
    public required string Name { get; init; }
    public required string ShortName { get; init; }
    public required string WeldLabel { get; init; }
    public required int Number { get; init; }
    public required bool IsVideo { get; init; }
    public required long Length { get; init; }
    public required DateTime Modified { get; init; }

    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set
        {
            _thumbnail = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail)));
        }
    }

    public string SizeText => Length >= 1048576 ? $"{Length / 1048576.0:0.0} MB" : $"{Math.Max(1, Length / 1024)} KB";

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Media files in the folder, in weld order (W1, W2, W2a, FW3, W16.1 …) then by n.</summary>
    public static List<PhotoItem> List(string folder)
    {
        var items = new List<PhotoItem>();
        foreach (var f in new DirectoryInfo(folder).GetFiles())
        {
            if (!MediaFiles.IsImportable(f)) continue;
            var m = NameRx.Match(f.Name);
            var number = m.Success && int.TryParse(m.Groups[3].Value, out var n) ? n : 0;
            items.Add(new PhotoItem
            {
                Path = f.FullName,
                Name = f.Name,
                ShortName = m.Success ? $"{m.Groups[2].Value}-{number}{m.Groups[4].Value.ToLowerInvariant()}" : f.Name,
                WeldLabel = m.Success ? m.Groups[2].Value : OtherLabel,
                Number = number,
                IsVideo = MediaFiles.IsVideo(f.Name),
                Length = f.Length,
                Modified = f.LastWriteTime,
            });
        }
        return items
            .OrderBy(i => i.WeldLabel == OtherLabel ? 1 : 0)
            .ThenBy(i => WeldSortKey(i.WeldLabel))
            .ThenBy(i => i.WeldLabel, StringComparer.Ordinal)
            .ThenBy(i => i.Number)
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static (double Number, string Repair, string Field) WeldSortKey(string label)
    {
        var m = WeldRx.Match(label);
        return m.Success && double.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
            ? (n, m.Groups[3].Value, m.Groups[1].Value)
            : (double.MaxValue, "", label);
    }
}

/// <summary>Decodes images for the viewer. Files are read into memory first, so nothing on
/// the share stays open (an open handle is what keeps Thumbs.db folders undeletable).</summary>
public static class ImageLoader
{
    public sealed record Preview(BitmapSource Image, int Width, int Height, DateTime? Taken);

    public static BitmapSource? TryLoad(string path, int decodeWidth)
    {
        try
        {
            return Decode(File.ReadAllBytes(path), decodeWidth);
        }
        catch
        {
            return null;
        }
    }

    public static Preview? LoadPreview(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            int width = 0, height = 0;
            using (var data = SKData.CreateCopy(bytes))
            using (var codec = SKCodec.Create(data))
            {
                if (codec != null)
                {
                    width = codec.Info.Width;
                    height = codec.Info.Height;
                }
            }
            DateTime? taken = null;
            try
            {
                taken = MediaFiles.ReadDateTimeOriginal(path);
            }
            catch
            {
            }
            // Large enough for a full-screen preview, small enough not to hold 50 MP in memory.
            var image = Decode(bytes, width > 0 ? Math.Min(width, 2400) : 2400);
            return new Preview(image, width, height, taken);
        }
        catch
        {
            return null;
        }
    }

    private static BitmapSource Decode(byte[] bytes, int decodeWidth)
    {
        using var stream = new MemoryStream(bytes);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        if (decodeWidth > 0) bitmap.DecodePixelWidth = decodeWidth;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }
}
