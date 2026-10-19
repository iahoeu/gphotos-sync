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

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_config.GiteaServerUrl) &&
        !string.IsNullOrWhiteSpace(_config.GiteaRepoOwner) &&
        !string.IsNullOrWhiteSpace(_config.GiteaRepoName);

    /// <summary>
    /// Ověří, že URL pro aktualizaci používá výhradně protokol HTTPS a směřuje na nakonfigurovaný Gitea server (P2-13).
    /// </summary>
    public static bool IsAllowedUpdateUrl(string? candidateUrl, string? configuredGiteaServerUrl)
    {
        if (string.IsNullOrWhiteSpace(candidateUrl) || string.IsNullOrWhiteSpace(configuredGiteaServerUrl))
            return false;

        if (!Uri.TryCreate(configuredGiteaServerUrl.Trim(), UriKind.Absolute, out var baseUri) ||
            !string.Equals(baseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!Uri.TryCreate(candidateUrl.Trim(), UriKind.Absolute, out var candidateUri) ||
            !string.Equals(candidateUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return string.Equals(candidateUri.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase) &&
               candidateUri.Port == baseUri.Port;
    }

    /// <summary>
    /// Vybere URL instalátoru z pole assets v Gitea release odpovědi a ověří, že splňuje bezpečnostní pravidla (P2-13).
    /// </summary>
    public static string SelectInstallerAssetUrl(JsonElement assetsArray, string? configuredGiteaServerUrl)
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

            if (!IsAllowedUpdateUrl(url, configuredGiteaServerUrl))
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
        HttpCompletionOption completionOption,
        CancellationToken ct)
    {
        string currentUrl = initialUrl;
        const int maxRedirects = 5;

        for (int i = 0; i <= maxRedirects; i++)
        {
            if (!IsAllowedUpdateUrl(currentUrl, _config.GiteaServerUrl))
            {
                throw new InvalidOperationException(
                    $"Nepovolená URL pro aktualizaci: '{currentUrl}'. Povoleno je pouze HTTPS na nakonfigurovaném Gitea serveru.");
            }

            using var req = new HttpRequestMessage(HttpMethod.Get, currentUrl);
            if (!string.IsNullOrWhiteSpace(userAgentVersion))
            {
                req.Headers.UserAgent.Add(new ProductInfoHeaderValue("GPhotosSync", userAgentVersion));
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

        throw new InvalidOperationException("Příliš mnoho přesměrování při komunikaci s Gitea serverem.");
    }

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken ct = default)
    {
        var currentVer = GetCurrentVersion();
        var currentVerStr = $"{currentVer.Major}.{currentVer.Minor}.{Math.Max(0, currentVer.Build)}";

        if (!IsConfigured)
        {
            return new UpdateCheckResult
            {
                IsUpdateAvailable = false,
                CurrentVersion = currentVerStr,
                ErrorMessage = "Gitea server pro aktualizace zatím není vyplněn v Nastavení."
            };
        }

        try
        {
            var baseUrl = _config.GiteaServerUrl.Trim().TrimEnd('/');
            if (!IsAllowedUpdateUrl(baseUrl, _config.GiteaServerUrl))
            {
                return new UpdateCheckResult
                {
                    IsUpdateAvailable = false,
                    CurrentVersion = currentVerStr,
                    ErrorMessage = "URL Gitea serveru musí začínat na https://."
                };
            }

            var owner = _config.GiteaRepoOwner.Trim();
            var repo = _config.GiteaRepoName.Trim();

            var apiUrl = $"{baseUrl}/api/v1/repos/{owner}/{repo}/releases/latest";

            using var resp = await SendWithSafeRedirectAsync(apiUrl, currentVerStr, HttpCompletionOption.ResponseContentRead, ct);
            if (!resp.IsSuccessStatusCode)
            {
                return new UpdateCheckResult
                {
                    IsUpdateAvailable = false,
                    CurrentVersion = currentVerStr,
                    ErrorMessage = $"Gitea API vrátilo stav {(int)resp.StatusCode} ({resp.ReasonPhrase})."
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
                downloadUrl = SelectInstallerAssetUrl(assetsProp, _config.GiteaServerUrl);
            }

            bool isNewer = latestVer > new Version(currentVer.Major, currentVer.Minor, Math.Max(0, currentVer.Build));

            if (isNewer && string.IsNullOrWhiteSpace(downloadUrl))
            {
                return new UpdateCheckResult
                {
                    IsUpdateAvailable = false,
                    CurrentVersion = currentVerStr,
                    LatestVersion = $"{latestVer.Major}.{latestVer.Minor}.{Math.Max(0, latestVer.Build)}",
                    ErrorMessage = "Byla nalezena nová verze, ale neobsahuje platný instalátor na povolené HTTPS doméně Gitea serveru."
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
            Logger.Warn($"Kontrola aktualizací na Gitea serveru selhala: {ex.Message}");
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
            if (!IsAllowedUpdateUrl(downloadUrl, _config.GiteaServerUrl))
            {
                Logger.Error($"Aktualizace zablokována: URL '{downloadUrl}' není na povoleném HTTPS Gitea serveru.");
                return false;
            }

            Logger.Info($"Stahuji novou verzi instalátoru z Gitea: {downloadUrl}");

            var currentVer = GetCurrentVersion();
            var currentVerStr = $"{currentVer.Major}.{currentVer.Minor}.{Math.Max(0, currentVer.Build)}";

            using var resp = await SendWithSafeRedirectAsync(downloadUrl, currentVerStr, HttpCompletionOption.ResponseHeadersRead, ct);
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
