using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace IMPWeldPhotos;

/// <summary>
/// Updates the app from its GitHub releases. A running exe can't be overwritten, but it can be
/// renamed: the current file becomes {exe}.old, the download takes its name, the new exe starts
/// and this one exits. The next start deletes the .old file.
/// </summary>
public static class Updater
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/petarcrncecIMP/IMP-Weld-Inspection/releases/latest";

    /// <summary>Passed to the new exe so it waits for this one to close instead of treating it
    /// as a second copy.</summary>
    public const string AfterUpdateArg = "--after-update";

    public sealed record Release(Version Version, string Tag, string DownloadUrl, long Size);

    public static Version CurrentVersion => Normalize(Assembly.GetEntryAssembly()?.GetName().Version);

    /// <summary>Local builds are 0.0.0; only release builds get their version from the tag.</summary>
    public static bool IsDevBuild => CurrentVersion == new Version(0, 0, 0);

    /// <summary>The newer release, or null when there is none or GitHub can't be reached.</summary>
    public static async Task<Release?> CheckAsync(CancellationToken ct)
    {
        if (IsDevBuild) return null;
        try
        {
            using var http = NewClient(TimeSpan.FromSeconds(20));
            using var response = await http.GetAsync(LatestReleaseUrl, ct);
            if (!response.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
            if (NewerThan(CurrentVersion, tag) is not { } version) return null;

            foreach (var asset in doc.RootElement.GetProperty("assets").EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                return new Release(version, tag, asset.GetProperty("browser_download_url").GetString() ?? "",
                                   asset.GetProperty("size").GetInt64());
            }
            return null;
        }
        catch
        {
            // Offline, rate-limited or blocked: just no update offer.
            return null;
        }
    }

    /// <summary>The tag's version when it is newer than current, else null. "v1.0.10" is newer
    /// than 1.0.9; a tag that isn't a version never is.</summary>
    public static Version? NewerThan(Version current, string tag)
    {
        if (!Version.TryParse(tag.Trim().TrimStart('v', 'V'), out var parsed)) return null;
        var version = Normalize(parsed);
        return version > Normalize(current) ? version : null;
    }

    /// <summary>Downloads and swaps in the release, then starts it. On success the caller must
    /// shut down; on failure nothing has changed.</summary>
    public static async Task InstallAsync(Release release, IProgress<double>? progress, CancellationToken ct)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("pot do programa ni znana");
        await DownloadAndSwapAsync(release, exe, progress, ct);
        Process.Start(new ProcessStartInfo(exe, AfterUpdateArg)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(exe),
        });
    }

    /// <summary>Downloads the release beside exe, checks it is complete, and swaps the files:
    /// exe becomes exe.old and the download takes its name. Any failure leaves exe as it was
    /// and no .download file behind.</summary>
    public static async Task DownloadAndSwapAsync(Release release, string exe, IProgress<double>? progress, CancellationToken ct)
    {
        var part = exe + ".download";
        var old = exe + ".old";
        try
        {
            using var http = NewClient(TimeSpan.FromMinutes(15));
            using var response = await http.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            await using (var from = await response.Content.ReadAsStreamAsync(ct))
            await using (var to = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[1 << 16];
                long done = 0;
                int read;
                while ((read = await from.ReadAsync(buffer, ct)) > 0)
                {
                    await to.WriteAsync(buffer.AsMemory(0, read), ct);
                    done += read;
                    progress?.Report(release.Size > 0 ? (double)done / release.Size : 0);
                }
            }
            if (new FileInfo(part).Length != release.Size)
                throw new IOException("prenos ni popoln");

            if (File.Exists(old)) File.Delete(old);
            File.Move(exe, old);
            try
            {
                File.Move(part, exe);
            }
            catch
            {
                File.Move(old, exe);
                throw;
            }
        }
        catch
        {
            TryDelete(part);
            throw;
        }
    }

    /// <summary>Removes the previous exe left behind by an update.</summary>
    public static void DeleteLeftover(string? exe = null)
    {
        exe ??= Environment.ProcessPath;
        if (exe != null) TryDelete(exe + ".old");
    }

    /// <summary>Right after an update the previous copy is often still closing and holds its
    /// file, so one attempt at start leaves the .old lying beside the exe. Keep trying in the
    /// background for about half a minute.</summary>
    public static async Task DeleteLeftoverSoonAsync(string? exe = null)
    {
        exe ??= Environment.ProcessPath;
        if (exe == null) return;
        for (var attempt = 0; attempt < 15 && File.Exists(exe + ".old"); attempt++)
        {
            TryDelete(exe + ".old");
            if (!File.Exists(exe + ".old")) return;
            await Task.Delay(2000);
        }
    }

    private static HttpClient NewClient(TimeSpan timeout)
    {
        var http = new HttpClient { Timeout = timeout };
        // GitHub's API refuses requests without a User-Agent.
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("IMPWeldInspection", CurrentVersion.ToString()));
        return http;
    }

    private static Version Normalize(Version? v) =>
        v == null ? new Version(0, 0, 0) : new Version(v.Major, v.Minor, Math.Max(v.Build, 0));

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Still locked (the old exe may take a moment to exit); the next start tries again.
        }
    }
}
