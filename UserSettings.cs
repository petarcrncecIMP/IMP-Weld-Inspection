using System.Text.Json;
using Microsoft.Win32;

namespace IMPWeldPhotos;

/// <summary>Per-user choices remembered between runs: theme, window placement.</summary>
public sealed class UserSettings
{
    public string? Theme { get; set; }
    public double? Left { get; set; }
    public double? Top { get; set; }
    public double? Width { get; set; }
    public double? Height { get; set; }
    public bool Maximized { get; set; }

    /// <summary>Project folder last picked in Pregled.</summary>
    public string? ViewerProject { get; set; }

    /// <summary>Number of the last report made on this PC; the next one is suggested from it.</summary>
    public string? LastReportNo { get; set; }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IMP", "IMPWeldPhotos", "settings.json");

    public static UserSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(FilePath)) ?? new UserSettings();
        }
        catch
        {
        }
        return new UserSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Losing the window position is not worth bothering anyone about.
        }
    }

    /// <summary>The Windows app theme, used until the user picks one.</summary>
    public static bool WindowsUsesDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch
        {
            return false;
        }
    }
}
