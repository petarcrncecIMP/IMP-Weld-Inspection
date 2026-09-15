using MetadataExtractor;
using MetadataExtractor.Formats.Exif;

namespace IMPWeldPhotos;

/// <summary>Which card files are endoscope photos or videos, and when they were taken.</summary>
public static class MediaFiles
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp",
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".avi", ".mov",
    };

    public static bool IsImage(string name) => ImageExtensions.Contains(Path.GetExtension(name));

    public static bool IsVideo(string name) => VideoExtensions.Contains(Path.GetExtension(name));

    public static bool IsImportable(FileInfo file)
    {
        if (file.Name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase) ||
            file.Name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
            return false;
        if ((file.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) return false;
        return IsImage(file.Name) || IsVideo(file.Name);
    }

    /// <summary>EXIF DateTimeOriginal, or null when the file has none. I/O errors are
    /// left to the caller, which needs them to notice a card being pulled.</summary>
    public static DateTime? ReadDateTimeOriginal(string path)
    {
        try
        {
            foreach (var dir in ImageMetadataReader.ReadMetadata(path).OfType<ExifSubIfdDirectory>())
            {
                if (dir.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var taken)) return taken;
            }
        }
        catch (ImageProcessingException)
        {
        }
        catch (MetadataException)
        {
        }
        return null;
    }
}
