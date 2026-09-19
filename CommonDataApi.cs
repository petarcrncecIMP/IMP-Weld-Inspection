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

/// <summary>A query needed a sign-in the user hasn't done (or that has expired).</summary>
public sealed class NotSignedInException : Exception
{
    public NotSignedInException() : base("niste prijavljeni v CommonData")
    {
    }
}

/// <summary>
/// CommonData OData access for the person running the app, signed in the way
/// IMPPromont.CommonData.Client's ServiceClient does it: MSAL public client, http://localhost
/// redirect, and the same token cache file, so the AutoCAD tools' sign-in is reused here.
///
/// Sign-in is explicit. SignInSilentAsync runs at start; SignInInteractiveAsync runs from the
/// header button. Queries never open a browser: without a sign-in they throw
/// NotSignedInException and the resolver falls back to titles.json.
///
/// Not the package itself: its HttpClient and request builder are internal, and no version
/// reachable from here has a call for FabBomIsoView.
/// </summary>
public sealed class CommonDataApi : IDisposable
{
    private const int CodesPerQuery = 20;
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http;
    private readonly IPublicClientApplication _pca;
    private readonly string[] _scopes;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private IAccount? _account;

    /// <summary>The signed-in account (e.g. name@imp-pro-mont.si), or null.</summary>
    public string? UserName { get; private set; }

    /// <summary>Set when the API refused this user (no Fab roles); cleared by a new sign-in.</summary>
    public string? DisabledReason { get; private set; }

    public bool IsSignedIn => UserName != null && DisabledReason == null;

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

    public enum SilentResult
    {
        SignedIn,
        /// <summary>No account on this PC, or its sign-in has expired: a login is needed.</summary>
        NeedsSignIn,
        /// <summary>Entra or the network didn't answer; a login wouldn't help now.</summary>
        Unreachable,
    }

    /// <summary>Signs in from the token cache without any window.</summary>
    public async Task<SilentResult> TrySignInSilentAsync(CancellationToken ct)
    {
        try
        {
            await TokenAsync(interactive: false, ct);
            return SilentResult.SignedIn;
        }
        catch (NotSignedInException)
        {
            return SilentResult.NeedsSignIn;
        }
        catch (Exception)
        {
            return SilentResult.Unreachable;
        }
    }

    public async Task<bool> SignInSilentAsync(CancellationToken ct) =>
        await TrySignInSilentAsync(ct) == SilentResult.SignedIn;

    /// <summary>Opens the system browser to sign in (or pick another account).</summary>
    public async Task SignInInteractiveAsync(CancellationToken ct)
    {
        await TokenAsync(interactive: true, ct);
        DisabledReason = null;
    }

    /// <summary>FabBomIsoView rows for the given BOM codes, CodesPerQuery codes per request.</summary>
    public async Task<List<BomIsoRow>> GetBomIsoRowsAsync(IReadOnlyCollection<int> bomCodes, CancellationToken ct)
    {
        var rows = new List<BomIsoRow>();
        foreach (var chunk in bomCodes.Distinct().Chunk(CodesPerQuery))
        {
            var filter = string.Join(" or ", chunk.Select(c => $"BomCode eq {c}"));
            // Leading slash, as FabClient does: it resolves against the host, so a BaseUrl
            // ending in /api still reaches /odata.
            var url = $"/odata/FabBomIsoView?$filter={Uri.EscapeDataString(filter)}" +
                      "&$select=BomCode,BomName,ProjectCode,ProjectName,UnitCode,UnitName";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(interactive: false, ct));
            using var response = await _http.SendAsync(request, ct);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                DisabledReason = "za CommonData nimate pravic (potrebni vlogi Fab.Read in Fab.Odata)";
                throw new HttpRequestException(DisabledReason);
            }
            response.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var items = doc.RootElement.TryGetProperty("value", out var value) ? value : doc.RootElement;
            rows.AddRange(items.Deserialize<List<BomIsoRow>>(Json) ?? new List<BomIsoRow>());
        }
        return rows;
    }

    private async Task<string> TokenAsync(bool interactive, CancellationToken ct)
    {
        await _tokenLock.WaitAsync(ct);
        try
        {
            AuthenticationResult result;
            if (interactive)
            {
                result = await _pca.AcquireTokenInteractive(_scopes)
                    .WithPrompt(Prompt.SelectAccount)
                    .WithUseEmbeddedWebView(false)
                    .ExecuteAsync(ct);
            }
            else
            {
                var account = _account ?? (await _pca.GetAccountsAsync()).FirstOrDefault();
                try
                {
                    result = await _pca.AcquireTokenSilent(_scopes, account).ExecuteAsync(ct);
                }
                catch (MsalUiRequiredException)
                {
                    _account = null;
                    UserName = null;
                    throw new NotSignedInException();
                }
            }
            _account = result.Account;
            UserName = result.Account?.Username;
            return result.AccessToken;
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
        if (inner is TaskCanceledException) return "CommonData se ni odzval v 30 sekundah";
        var m = inner.Message ?? "";
        if (m.Contains("refused", StringComparison.OrdinalIgnoreCase)) return "CommonData ne teče (povezava zavrnjena)";
        if (m.Contains("No such host", StringComparison.OrdinalIgnoreCase)) return "imena strežnika CommonData ni mogoče razrešiti";
        return m;
    }

    public void Dispose()
    {
        _http.Dispose();
        _tokenLock.Dispose();
    }
}
