using System.Text.Json;

namespace IMPWeldPhotos;

/// <summary>Read from IMP-Weld-Inspection.config.json beside the exe. Optional: every
/// value has a default, the CommonData connection included.</summary>
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
            cfg.Api.FillBlanks();
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

/// <summary>CommonData connection. The defaults are production CommonData with the app
/// registration the AutoCAD tools use (Autodesk_ext\Plant3D\ApiConfig.vb), chosen by the
/// user on 2026-09-19. The config file can override any value (a blank one keeps the
/// default) or switch the database off with "Enabled": false.</summary>
public sealed class ApiSettings
{
    private const string DefaultBaseUrl = "https://impp-commondata.azurewebsites.net/api";
    private const string DefaultTenantId = "5316bd8d-6644-41d8-9fa4-1451be73b785";
    private const string DefaultClientId = "52757799-fad4-4ea7-96ac-3172691f1fbd";
    private const string DefaultScope = "api://5de225d4-c2ef-44d6-ab7e-5b947f040b12/access_as_user";

    public bool Enabled { get; set; } = true;
    public string BaseUrl { get; set; } = DefaultBaseUrl;
    public string TenantId { get; set; } = DefaultTenantId;
    public string ClientId { get; set; } = DefaultClientId;
    public string Scope { get; set; } = DefaultScope;

    public bool IsConfigured =>
        Enabled && !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(TenantId) &&
        !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(Scope);

    /// <summary>A config file written before the defaults existed has empty strings here.</summary>
    public void FillBlanks()
    {
        if (string.IsNullOrWhiteSpace(BaseUrl)) BaseUrl = DefaultBaseUrl;
        if (string.IsNullOrWhiteSpace(TenantId)) TenantId = DefaultTenantId;
        if (string.IsNullOrWhiteSpace(ClientId)) ClientId = DefaultClientId;
        if (string.IsNullOrWhiteSpace(Scope)) Scope = DefaultScope;
    }
}
