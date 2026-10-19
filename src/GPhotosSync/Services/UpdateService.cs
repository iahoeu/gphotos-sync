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
    private static readonly HttpClient Http = new()
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
            var owner = _config.GiteaRepoOwner.Trim();
            var repo = _config.GiteaRepoName.Trim();

            var apiUrl = $"{baseUrl}/api/v1/repos/{owner}/{repo}/releases/latest";

            using var req = new HttpRequestMessage(HttpMethod.Get, apiUrl);
            req.Headers.UserAgent.Add(new ProductInfoHeaderValue("GPhotosSync", currentVerStr));
            if (!string.IsNullOrWhiteSpace(_config.GiteaAccessToken))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("token", _config.GiteaAccessToken.Trim());
            }

            using var resp = await Http.SendAsync(req, ct);
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
                foreach (var asset in assetsProp.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var nProp) ? nProp.GetString() ?? "" : "";
                    var url = asset.TryGetProperty("browser_download_url", out var uProp) ? uProp.GetString() ?? "" : "";

                    if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(url))
                    {
                        downloadUrl = url;
                        if (name.Contains("Setup", StringComparison.OrdinalIgnoreCase))
                            break;
                    }
                }
            }

            bool isNewer = latestVer > new Version(currentVer.Major, currentVer.Minor, Math.Max(0, currentVer.Build));

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
            Logger.Info($"Stahuji novou verzi instalátoru z Gitea: {downloadUrl}");

            using var req = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
            if (!string.IsNullOrWhiteSpace(_config.GiteaAccessToken))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("token", _config.GiteaAccessToken.Trim());
            }

            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
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
