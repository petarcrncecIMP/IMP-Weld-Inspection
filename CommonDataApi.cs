using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Identity.Client;

namespace IMPWeldPhotos;

public sealed class BomIsoRow
{
    public int BomCode { get; set; }
    public string? BomName { get; set; }
    public string? ProjectCode { get; set; }
    public string? ProjectName { get; set; }
    public int? UnitCode { get; set; }
    public string? UnitName { get; set; }
}

/// <summary>
/// CommonData OData access, signed in the way IMPPromont.CommonData.Client's
/// ServiceClient(baseUrl, tenantId, clientId, scopes) does it: MSAL public client,
/// http://localhost redirect, silent first and interactive when needed, and the same
/// token cache file, so a sign-in from another IMP tool is reused here.
///
/// Not the package itself: its HttpClient and request builder are internal, and no
/// version reachable from here has a call for FabBomIsoView.
/// </summary>
public sealed class CommonDataApi : IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http;
    private readonly IPublicClientApplication _pca;
    private readonly string[] _scopes;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    /// <summary>Set once the API can't be used for the rest of the session (sign-in
    /// cancelled, no permission), so the user isn't asked again for every BOM.</summary>
    public string? DisabledReason { get; private set; }

    public CommonDataApi(ApiSettings settings)
    {
        _http = new HttpClient { BaseAddress = new Uri(settings.BaseUrl), Timeout = TimeSpan.FromSeconds(30) };
        _scopes = new[] { settings.Scope };
        _pca = PublicClientApplicationBuilder
            .Create(settings.ClientId)
            .WithAuthority(AzureCloudInstance.AzurePublic, settings.TenantId)
            .WithRedirectUri("http://localhost")
            .Build();
        AttachPersistentTokenCache(_pca.UserTokenCache);
    }

    public async Task<List<BomIsoRow>> GetBomIsoRowsAsync(int bomCode, CancellationToken ct)
    {
        // Leading slash, as FabClient does: it resolves against the host, so a
        // BaseUrl ending in /api still reaches /odata.
        var url = $"/odata/FabBomIsoView?$filter=BomCode eq {bomCode}" +
                  "&$select=BomCode,BomName,ProjectCode,ProjectName,UnitCode,UnitName";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await GetTokenAsync(ct));
        using var response = await _http.SendAsync(request, ct);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            DisabledReason = "za CommonData API nimate pravic (potrebni vlogi Fab.Read in Fab.Odata)";
            throw new HttpRequestException(DisabledReason);
        }
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var items = doc.RootElement.TryGetProperty("value", out var value) ? value : doc.RootElement;
        return items.Deserialize<List<BomIsoRow>>(Json) ?? new List<BomIsoRow>();
    }

    private async Task<string> GetTokenAsync(CancellationToken ct)
    {
        await _tokenLock.WaitAsync(ct);
        try
        {
            var accounts = await _pca.GetAccountsAsync();
            try
            {
                return (await _pca.AcquireTokenSilent(_scopes, accounts.FirstOrDefault()).ExecuteAsync(ct)).AccessToken;
            }
            catch (MsalUiRequiredException)
            {
                try
                {
                    return (await _pca.AcquireTokenInteractive(_scopes)
                        .WithPrompt(Prompt.SelectAccount)
                        .WithUseEmbeddedWebView(false)
                        .ExecuteAsync(ct)).AccessToken;
                }
                catch (MsalClientException ex) when (ex.ErrorCode == MsalError.AuthenticationCanceledError)
                {
                    DisabledReason = "prijava v CommonData je bila preklicana";
                    throw;
                }
            }
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    /// <summary>Same cache file as ServiceClient.AttachPersistentTokenCache.</summary>
    private static void AttachPersistentTokenCache(ITokenCache tokenCache)
    {
        var cacheDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IMPPromont.CommonData");
        Directory.CreateDirectory(cacheDirectory);
        var cacheFilePath = Path.Combine(cacheDirectory, "msal_cache.bin");

        tokenCache.SetBeforeAccess(args =>
        {
            if (File.Exists(cacheFilePath))
                args.TokenCache.DeserializeMsalV3(File.ReadAllBytes(cacheFilePath), shouldClearExistingCache: true);
        });
        tokenCache.SetAfterAccess(args =>
        {
            if (args.HasStateChanged)
                File.WriteAllBytes(cacheFilePath, args.TokenCache.SerializeMsalV3());
        });
    }

    /// <summary>One line for the user, as CommonDataApi.ClassifyError does in the
    /// AutoCAD tools.</summary>
    public static string Describe(Exception ex)
    {
        var inner = ex;
        while (inner.InnerException != null) inner = inner.InnerException;
        if (inner is TaskCanceledException) return "API se ni odzval v 30 sekundah";
        var m = inner.Message ?? "";
        if (m.Contains("refused", StringComparison.OrdinalIgnoreCase)) return "API ne teče (povezava zavrnjena)";
        if (m.Contains("No such host", StringComparison.OrdinalIgnoreCase)) return "imena strežnika API ni mogoče razrešiti";
        return m;
    }

    public void Dispose()
    {
        _http.Dispose();
        _tokenLock.Dispose();
    }
}
