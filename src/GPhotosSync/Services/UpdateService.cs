using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using GPhotosSync.Data;

namespace GPhotosSync.Services;

public class UpdateCheckResult
{
    public bool IsUpdateAvailable { get; set; }
    public string CurrentVersion { get; set; } = string.Empty;
    public string LatestVersion { get; set; } = string.Empty;
    public string ReleaseNotes { get; set; } = string.Empty;
    public string DownloadUrl { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
}

public class UpdateService
{
    private readonly AppConfig _config;
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false
    })
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    public UpdateService(AppConfig config)
    {
        _config = config;
    }

    public static Version GetCurrentVersion()
    {
        return Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);
    }

    private const string RepoOwner = "iahoeu";
    private const string RepoName = "gphotos-sync";
    private const string LatestReleaseApiUrl = "https://api.github.com/repos/" + RepoOwner + "/" + RepoName + "/releases/latest";
    private const string ReleaseDownloadPrefix = "https://github.com/" + RepoOwner + "/" + RepoName + "/releases/download/";

    private static readonly string[] AllowedHosts =
    {
        "api.github.com",
        "github.com",
        "objects.githubusercontent.com",
        "release-assets.githubusercontent.com"
    };

    /// <summary>
    /// Ověří, že URL pro aktualizaci používá výhradně HTTPS (port 443) a směřuje na doménu GitHubu (P2-13).
    /// </summary>
    public static bool IsAllowedUpdateUrl(string? candidateUrl)
    {
        if (string.IsNullOrWhiteSpace(candidateUrl))
            return false;

        if (!Uri.TryCreate(candidateUrl.Trim(), UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            uri.Port != 443)
        {
            return false;
        }

        return AllowedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Vybere URL instalátoru z pole assets v GitHub release odpovědi a ověří, že splňuje bezpečnostní pravidla (P2-13).
    /// </summary>
    public static string SelectInstallerAssetUrl(JsonElement assetsArray)
    {
        if (assetsArray.ValueKind != JsonValueKind.Array)
            return string.Empty;

        string fallbackExeUrl = string.Empty;

        foreach (var asset in assetsArray.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var nProp) ? nProp.GetString() ?? "" : "";
            var url = asset.TryGetProperty("browser_download_url", out var uProp) ? uProp.GetString() ?? "" : "";

            if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(url))
                continue;

            if (!IsAllowedUpdateUrl(url) ||
                !url.Trim().StartsWith(ReleaseDownloadPrefix, StringComparison.OrdinalIgnoreCase))
                continue;

            if (name.Contains("Setup", StringComparison.OrdinalIgnoreCase))
                return url;

            if (string.IsNullOrEmpty(fallbackExeUrl))
                fallbackExeUrl = url;
        }

        return fallbackExeUrl;
    }

    private async Task<HttpResponseMessage> SendWithSafeRedirectAsync(
        string initialUrl,
        string? userAgentVersion,
        bool isApiCall,
        HttpCompletionOption completionOption,
        CancellationToken ct)
    {
        string currentUrl = initialUrl;
        const int maxRedirects = 5;

        for (int i = 0; i <= maxRedirects; i++)
        {
            if (!IsAllowedUpdateUrl(currentUrl))
            {
                throw new InvalidOperationException(
                    $"Nepovolená URL pro aktualizaci: '{currentUrl}'. Povoleno je pouze HTTPS na doménách GitHubu.");
            }

            using var req = new HttpRequestMessage(HttpMethod.Get, currentUrl);
            if (!string.IsNullOrWhiteSpace(userAgentVersion))
            {
                req.Headers.UserAgent.Add(new ProductInfoHeaderValue("GPhotosSync", userAgentVersion));
            }
            if (isApiCall)
            {
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
                req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            }
            else
            {
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
            }

            var resp = await Http.SendAsync(req, completionOption, ct);
            int status = (int)resp.StatusCode;
            if (status is 301 or 302 or 303 or 307 or 308 && resp.Headers.Location != null)
            {
                var nextUri = resp.Headers.Location.IsAbsoluteUri
                    ? resp.Headers.Location
                    : new Uri(new Uri(currentUrl), resp.Headers.Location);
                resp.Dispose();
                currentUrl = nextUri.ToString();
                continue;
            }

            return resp;
        }

        throw new InvalidOperationException("Příliš mnoho přesměrování při komunikaci s GitHubem.");
    }

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken ct = default)
    {
        var currentVer = GetCurrentVersion();
        var currentVerStr = $"{currentVer.Major}.{currentVer.Minor}.{Math.Max(0, currentVer.Build)}";

        try
        {
            using var resp = await SendWithSafeRedirectAsync(LatestReleaseApiUrl, currentVerStr, true, HttpCompletionOption.ResponseContentRead, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var status = (int)resp.StatusCode;
                var hint = status is 403 or 429
                    ? " Limit požadavků GitHubu byl pravděpodobně překročen, kontrola se zopakuje později."
                    : "";
                return new UpdateCheckResult
                {
                    IsUpdateAvailable = false,
                    CurrentVersion = currentVerStr,
                    ErrorMessage = $"GitHub API vrátilo stav {status} ({resp.ReasonPhrase}).{hint}"
                };
            }

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tagName = root.TryGetProperty("tag_name", out var tagProp) ? tagProp.GetString() ?? "" : "";
            var body = root.TryGetProperty("body", out var bodyProp) ? bodyProp.GetString() ?? "" : "";

            var cleanTag = tagName.Trim().TrimStart('v', 'V');
            if (!Version.TryParse(cleanTag, out var latestVer))
            {
                return new UpdateCheckResult
                {
                    IsUpdateAvailable = false,
                    CurrentVersion = currentVerStr,
                    ErrorMessage = $"Nelze přečíst číslo verze z tagu '{tagName}'."
                };
            }

            string downloadUrl = "";
            if (root.TryGetProperty("assets", out var assetsProp) && assetsProp.ValueKind == JsonValueKind.Array)
            {
                downloadUrl = SelectInstallerAssetUrl(assetsProp);
            }

            bool isNewer = latestVer > new Version(currentVer.Major, currentVer.Minor, Math.Max(0, currentVer.Build));

            if (isNewer && string.IsNullOrWhiteSpace(downloadUrl))
            {
                return new UpdateCheckResult
                {
                    IsUpdateAvailable = false,
                    CurrentVersion = currentVerStr,
                    LatestVersion = $"{latestVer.Major}.{latestVer.Minor}.{Math.Max(0, latestVer.Build)}",
                    ErrorMessage = "Byla nalezena nová verze, ale neobsahuje platný instalátor na povolené HTTPS doméně GitHubu."
                };
            }

            return new UpdateCheckResult
            {
                IsUpdateAvailable = isNewer && !string.IsNullOrWhiteSpace(downloadUrl),
                CurrentVersion = currentVerStr,
                LatestVersion = $"{latestVer.Major}.{latestVer.Minor}.{Math.Max(0, latestVer.Build)}",
                ReleaseNotes = body,
                DownloadUrl = downloadUrl
            };
        }
        catch (Exception ex)
        {
            Logger.Warn($"Kontrola aktualizací na GitHubu selhala: {ex.Message}");
            return new UpdateCheckResult
            {
                IsUpdateAvailable = false,
                CurrentVersion = currentVerStr,
                ErrorMessage = ex.Message
            };
        }
    }

    public async Task<bool> DownloadAndApplyUpdateAsync(string downloadUrl, Action? onBeforeExit = null, CancellationToken ct = default)
    {
        try
        {
            if (!IsAllowedUpdateUrl(downloadUrl))
            {
                Logger.Error($"Aktualizace zablokována: URL '{downloadUrl}' není na povolené HTTPS doméně GitHubu.");
                return false;
            }

            Logger.Info($"Stahuji novou verzi instalátoru z GitHubu: {downloadUrl}");

            var currentVer = GetCurrentVersion();
            var currentVerStr = $"{currentVer.Major}.{currentVer.Minor}.{Math.Max(0, currentVer.Build)}";

            using var resp = await SendWithSafeRedirectAsync(downloadUrl, currentVerStr, false, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();

            var tempUpdateExe = Path.Combine(Path.GetTempPath(), $"GPhotosSync_Update_{Guid.NewGuid():N}.exe");
            await using (var fs = new FileStream(tempUpdateExe, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await resp.Content.CopyToAsync(fs, ct);
            }

            var currentInstallDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            Logger.Info($"Spouštím tichou aktualizaci do složky '{currentInstallDir}'...");

            Process.Start(new ProcessStartInfo(tempUpdateExe, $"--silent-update \"{currentInstallDir}\"")
            {
                UseShellExecute = true
            });

            onBeforeExit?.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error($"Chyba při stahování nebo spouštění aktualizace: {ex.Message}");
            return false;
        }
    }
}
