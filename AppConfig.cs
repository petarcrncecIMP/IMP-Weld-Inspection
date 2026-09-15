using System.Text.Json;

namespace IMPWeldPhotos;

/// <summary>Read from IMP-Weld-Inspection.config.json beside the exe. Every value has a
/// default except the API, which stays off until someone fills it in.</summary>
public sealed class AppConfig
{
    public const string FileName = "IMP-Weld-Inspection.config.json";

    /// <summary>The name before the app was renamed; still read when it's the one beside the exe.</summary>
    private const string OldFileName = "IMPWeldPhotos.config.json";

    public string DestinationRoot { get; set; } = @"U:\100_Identi\140_Zvari";
    public string IsoRoot { get; set; } = @"U:\100_Identi\130_Izometrije";
    public ApiSettings Api { get; set; } = new();
    public int PollSeconds { get; set; } = 3;

    public string TitlesPath => Path.Combine(IsoRoot, "titles.json");

    public static string LogFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IMP", "IMPWeldPhotos", "logs");

    public static AppConfig Load(out string? error)
    {
        error = null;
        var path = Path.Combine(AppContext.BaseDirectory, FileName);
        if (!File.Exists(path)) path = Path.Combine(AppContext.BaseDirectory, OldFileName);
        if (!File.Exists(path)) return new AppConfig();
        try
        {
            var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            }) ?? new AppConfig();
            cfg.Api ??= new ApiSettings();
            if (cfg.PollSeconds < 1) cfg.PollSeconds = 1;
            return cfg;
        }
        catch (Exception ex)
        {
            error = $"{FileName} ni berljiv, uporabljene so privzete nastavitve: {ex.Message}";
            return new AppConfig();
        }
    }

    /// <summary>Null when both roots are reachable, else a message for the user.
    /// Checked before any card is scanned.</summary>
    public string? CheckShare()
    {
        var destOk = Directory.Exists(DestinationRoot);
        var isoOk = Directory.Exists(IsoRoot);
        if (destOk && isoOk) return null;
        var drive = Path.GetPathRoot(DestinationRoot) ?? DestinationRoot;
        if (!destOk && !isoOk)
            return $"Pogon {drive} ni povezan ali ni dosegljiv. Povežite se z omrežjem podjetja in pritisnite Preglej znova.";
        return !destOk
            ? $"Ciljna mapa {DestinationRoot} ni dosegljiva."
            : $"Mapa izometrij {IsoRoot} ni dosegljiva.";
    }
}

public sealed class ApiSettings
{
    public string BaseUrl { get; set; } = "";
    public string TenantId { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string Scope { get; set; } = "";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(TenantId) &&
        !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(Scope);
}
