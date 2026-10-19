using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using GPhotosSync.Data;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace GPhotosSync.Services;

public class DiscoveredMediaItem
{
    public string MediaKey { get; set; } = string.Empty;
    public string PhotoUrl { get; set; } = string.Empty;
    public string AriaLabel { get; set; } = string.Empty;
    public bool IsVideo { get; set; }
    public DateTime? EstimatedDate { get; set; }
}

public class DownloadResult
{
    public bool Success { get; set; }
    public bool SkippedVideo { get; set; }
    public string LocalFileName { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public DateTime? TakenAt { get; set; }
    public string? ErrorMessage { get; set; }
}

public class GooglePhotosEngine : IDisposable
{
    private readonly Form _hostForm;
    private readonly WebView2 _webView;
    private CoreWebView2Environment? _env;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private bool _isInitialized;

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".m4v", ".avi", ".mkv", ".3gp", ".webm", ".mts", ".m2ts"
    };

    public GooglePhotosEngine()
    {
        // Hidden form hosting the background WebView2 instance
        _hostForm = new Form
        {
            Width = 1280,
            Height = 900,
            ShowInTaskbar = false,
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-30000, -30000),
            Opacity = 0
        };

        _webView = new WebView2
        {
            Dock = DockStyle.Fill
        };
        _hostForm.Controls.Add(_webView);

        // Force handle creation so WebView2 initializes even while hidden
        var _ = _hostForm.Handle;
    }

    public async Task EnsureInitializedAsync()
    {
        if (_isInitialized && _webView.CoreWebView2 != null)
            return;

        Directory.CreateDirectory(AppConfig.WebViewProfileFolder);
        _env = await CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null,
            userDataFolder: AppConfig.WebViewProfileFolder);

        if (!_hostForm.Visible)
        {
            _hostForm.Show();
            _hostForm.Hide();
        }

        await _webView.EnsureCoreWebView2Async(_env);
        _webView.CoreWebView2!.Settings.IsStatusBarEnabled = false;
        _webView.CoreWebView2.Settings.AreDefaultScriptDialogsEnabled = false;
        _webView.CoreWebView2.IsMuted = true;

        _isInitialized = true;
    }

    public async Task<CoreWebView2Environment> GetSharedEnvironmentAsync()
    {
        await EnsureInitializedAsync();
        return _env!;
    }

    public async Task<bool> CheckLoggedInAsync()
    {
        await _operationLock.WaitAsync();
        try
        {
            await EnsureInitializedAsync();
            bool navOk = await NavigateAndWaitAsync("https://photos.google.com/", 15000);
            if (!navOk) return false;

            await Task.Delay(1500);
            var url = _webView.CoreWebView2.Source ?? "";
            if (url.Contains("accounts.google.com", StringComparison.OrdinalIgnoreCase) ||
                url.Contains("/about", StringComparison.OrdinalIgnoreCase) ||
                url.Contains("workspace.google.com", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var checkJs = @"
                (() => {
                    const hasPhotos = document.querySelectorAll('a[href*=""/photo/""]').length > 0;
                    const hasAppShell = document.querySelector('c-wiz') !== null &&
                                        (document.querySelector('input[aria-label]') !== null ||
                                         document.querySelector('a[aria-label*=""Google""]') !== null ||
                                         document.querySelector('img[src*=""googleusercontent""]') !== null);
                    const hasSignInBtn = Array.from(document.querySelectorAll('a, button'))
                        .some(el => /Přihlásit|Sign in|Přejít do Fotek Google|Go to Google Photos/i.test(el.innerText || ''));
                    if (hasSignInBtn && !hasPhotos) return false;
                    return hasPhotos || hasAppShell;
                })();
            ";

            var result = await _webView.CoreWebView2.ExecuteScriptAsync(checkJs);
            return result == "true";
        }
        catch (Exception ex)
        {
            Logger.Warn($"Kontrola přihlášení selhala: {ex.Message}");
            return false;
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task<List<DiscoveredMediaItem>> ScanTimelineAsync(int maxScrollSteps = 5, CancellationToken ct = default)
    {
        await _operationLock.WaitAsync(ct);
        try
        {
            await EnsureInitializedAsync();
            var itemsById = new Dictionary<string, DiscoveredMediaItem>(StringComparer.Ordinal);

            bool navOk = await NavigateAndWaitAsync("https://photos.google.com/", 20000);
            if (!navOk) return [];

            // Wait for timeline items to render
            for (int wait = 0; wait < 12; wait++)
            {
                await Task.Delay(500, ct);
                var countJson = await _webView.CoreWebView2.ExecuteScriptAsync(
                    "document.querySelectorAll('a[href*=\"/photo/\"]').length");
                if (int.TryParse(countJson, out int c) && c > 0)
                    break;
            }

            for (int step = 0; step < maxScrollSteps; step++)
            {
                ct.ThrowIfCancellationRequested();

                var extractScript = @"
                    (() => {
                        const anchors = Array.from(document.querySelectorAll('a[href*=""/photo/""]'));
                        return anchors.map(a => {
                            const href = a.getAttribute('href') || '';
                            const label = a.getAttribute('aria-label') || '';
                            const isVid = /video|videa/i.test(label) || a.querySelector('[aria-label*=""Video""], [aria-label*=""video""]') !== null;
                            return { href, label, isVid };
                        });
                    })();
                ";

                var json = await _webView.CoreWebView2.ExecuteScriptAsync(extractScript);
                if (!string.IsNullOrWhiteSpace(json) && json != "null")
                {
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var elem in doc.RootElement.EnumerateArray())
                        {
                            var href = elem.GetProperty("href").GetString() ?? "";
                            var label = elem.GetProperty("label").GetString() ?? "";
                            var isVid = elem.GetProperty("isVid").GetBoolean();

                            var key = ExtractMediaKey(href);
                            if (string.IsNullOrEmpty(key) || itemsById.ContainsKey(key))
                                continue;

                            itemsById[key] = new DiscoveredMediaItem
                            {
                                MediaKey = key,
                                PhotoUrl = $"https://photos.google.com/photo/{key}",
                                AriaLabel = label,
                                IsVideo = isVid,
                                EstimatedDate = ParseDateFromAriaLabel(label)
                            };
                        }
                    }
                }

                if (step < maxScrollSteps - 1)
                {
                    await _webView.CoreWebView2.ExecuteScriptAsync(@"
                        (() => {
                            window.scrollBy(0, 1400);
                            const scrollers = Array.from(document.querySelectorAll('c-wiz, main, [role=""main""]'))
                                .filter(el => el.scrollHeight > el.clientHeight);
                            scrollers.forEach(el => el.scrollTop += 1400);
                        })();
                    ");
                    await Task.Delay(900, ct);
                }
            }

            return itemsById.Values.ToList();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Error($"Chyba při čtení časové osy Google Photos: {ex.Message}");
            return [];
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task<DownloadResult> DownloadMediaAsync(
        DiscoveredMediaItem item,
        string targetFolder,
        bool allowVideos,
        CancellationToken ct = default)
    {
        await _operationLock.WaitAsync(ct);
        try
        {
            await EnsureInitializedAsync();
            Directory.CreateDirectory(targetFolder);

            var tcsDownload = new TaskCompletionSource<DownloadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            string? tempFilePath = null;
            string? originalFileName = null;

            void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
            {
                try
                {
                    e.Handled = true; // Hide default browser download bar
                    originalFileName = Path.GetFileName(e.ResultFilePath);
                    if (string.IsNullOrWhiteSpace(originalFileName))
                    {
                        originalFileName = $"{item.MediaKey.Substring(0, Math.Min(12, item.MediaKey.Length))}.jpg";
                    }

                    var ext = Path.GetExtension(originalFileName);
                    if (!allowVideos && VideoExtensions.Contains(ext))
                    {
                        e.Cancel = true;
                        tcsDownload.TrySetResult(new DownloadResult
                        {
                            Success = false,
                            SkippedVideo = true,
                            LocalFileName = originalFileName
                        });
                        return;
                    }

                    tempFilePath = Path.Combine(targetFolder, $".{Guid.NewGuid():N}_{originalFileName}.part");
                    e.ResultFilePath = tempFilePath;

                    var op = e.DownloadOperation;
                    op.StateChanged += (_, _) =>
                    {
                        if (op.State == CoreWebView2DownloadState.Completed)
                        {
                            tcsDownload.TrySetResult(new DownloadResult
                            {
                                Success = true,
                                LocalFileName = originalFileName,
                                FullPath = tempFilePath
                            });
                        }
                        else if (op.State == CoreWebView2DownloadState.Interrupted)
                        {
                            tcsDownload.TrySetResult(new DownloadResult
                            {
                                Success = false,
                                ErrorMessage = $"Stahování přerušeno ({op.InterruptReason})"
                            });
                        }
                    };
                }
                catch (Exception ex)
                {
                    tcsDownload.TrySetResult(new DownloadResult
                    {
                        Success = false,
                        ErrorMessage = ex.Message
                    });
                }
            }

            _webView.CoreWebView2.DownloadStarting += OnDownloadStarting;
            try
            {
                bool navOk = await NavigateAndWaitAsync(item.PhotoUrl, 20000);
                if (!navOk)
                {
                    return new DownloadResult
                    {
                        Success = false,
                        ErrorMessage = "Nelze otevřít detail fotky v Google Photos."
                    };
                }

                await Task.Delay(1200, ct);

                // Trigger Shift+D via CDP (native Google Photos shortcut for downloading original file)
                await DispatchShiftDAsync();

                // Wait up to 4 seconds to see if download started; if not, try clicking UI Download button
                var startCheck = await Task.WhenAny(tcsDownload.Task, Task.Delay(4000, ct));
                if (startCheck != tcsDownload.Task && tempFilePath == null)
                {
                    await TriggerDownloadFromUiMenuAsync();
                }

                // Wait for download completion (up to 10 minutes for large videos)
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromMinutes(10));

                var completedTask = await Task.WhenAny(
                    tcsDownload.Task,
                    Task.Delay(Timeout.Infinite, timeoutCts.Token));

                if (completedTask != tcsDownload.Task)
                {
                    return new DownloadResult
                    {
                        Success = false,
                        ErrorMessage = "Časový limit stahování vypršel."
                    };
                }

                var res = await tcsDownload.Task;
                if (!res.Success || res.SkippedVideo)
                {
                    CleanupTempFile(tempFilePath);
                    return res;
                }

                // Move .part file to final unique filename in targetFolder
                var finalPath = GetUniqueFilePath(targetFolder, res.LocalFileName);
                File.Move(res.FullPath, finalPath, overwrite: true);

                var fileInfo = new FileInfo(finalPath);
                var takenAt = ExifService.ApplyCaptureTimestampToFile(finalPath, item.EstimatedDate);

                return new DownloadResult
                {
                    Success = true,
                    LocalFileName = Path.GetFileName(finalPath),
                    FullPath = finalPath,
                    FileSize = fileInfo.Length,
                    TakenAt = takenAt
                };
            }
            finally
            {
                _webView.CoreWebView2.DownloadStarting -= OnDownloadStarting;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new DownloadResult
            {
                Success = false,
                ErrorMessage = ex.Message
            };
        }
        finally
        {
            _operationLock.Release();
        }
    }

    /// <summary>
    /// Obousměrná synchronizace: Přesune fotku/video s daným MediaKey do koše v Google Photos.
    /// </summary>
    public async Task<bool> MoveToTrashAsync(string mediaKey, string photoUrl, CancellationToken ct = default)
    {
        await _operationLock.WaitAsync(ct);
        try
        {
            await EnsureInitializedAsync();
            var url = !string.IsNullOrWhiteSpace(photoUrl)
                ? photoUrl
                : $"https://photos.google.com/photo/{mediaKey}";

            Logger.Info($"Obousměrná synchronizace: Přesouvám položku '{mediaKey}' do koše v Google Photos...");

            bool navOk = await NavigateAndWaitAsync(url, 20000);
            if (!navOk) return false;

            await Task.Delay(1500, ct);

            // Check if already deleted / 404
            var notFoundCheck = await _webView.CoreWebView2.ExecuteScriptAsync(@"
                (() => {
                    const txt = document.body ? document.body.innerText : '';
                    return /Can't access photo|Nelze získat přístup|404|není k dispozici/i.test(txt);
                })();
            ");
            if (notFoundCheck == "true")
            {
                Logger.Info($"Položka '{mediaKey}' již v Google Photos neexistuje (byla smazána dříve).");
                return true;
            }

            // 1. Click Delete / Move to trash toolbar icon (or dispatch '#' key)
            var clickedToolbarTrash = await _webView.CoreWebView2.ExecuteScriptAsync(@"
                (() => {
                    const selectors = [
                        'button[aria-label*=""Smazat""]',
                        'button[aria-label*=""Přesunout do koše""]',
                        'button[aria-label*=""Delete""]',
                        'button[aria-label*=""Move to trash""]',
                        'button[aria-label*=""Move to bin""]',
                        '[data-delete-origin] button',
                        'div[role=""button""][aria-label*=""Smazat""]',
                        'div[role=""button""][aria-label*=""Delete""]'
                    ];
                    for (const sel of selectors) {
                        const btns = Array.from(document.querySelectorAll(sel));
                        const visible = btns.find(b => b.offsetParent !== null);
                        if (visible) {
                            visible.click();
                            return true;
                        }
                    }
                    return false;
                })();
            ");

            if (clickedToolbarTrash != "true")
            {
                // Fallback: Google Photos keyboard shortcut '#' moves open photo to trash
                await DispatchHashKeyAsync();
            }

            await Task.Delay(1000, ct);

            // 2. Confirm modal dialog ("Přesunout do koše" / "Move to trash")
            for (int attempt = 0; attempt < 5; attempt++)
            {
                var confirmed = await _webView.CoreWebView2.ExecuteScriptAsync(@"
                    (() => {
                        const dialogs = Array.from(document.querySelectorAll('[role=""dialog""], [role=""alertdialog""], .VfPpkd-P5QLlc'));
                        for (const dlg of dialogs) {
                            const buttons = Array.from(dlg.querySelectorAll('button, [role=""button""]'));
                            const confirmBtn = buttons.find(b => {
                                const t = (b.innerText || b.getAttribute('aria-label') || '').trim();
                                return /Přesunout do koše|Smazat|Move to trash|Move to bin|Delete|Got it/i.test(t);
                            }) || dlg.querySelector('button[data-mdc-dialog-action=""ok""]');

                            if (confirmBtn) {
                                confirmBtn.click();
                                return true;
                            }
                        }

                        // Global button search if dialog role is custom
                        const allBtns = Array.from(document.querySelectorAll('button'));
                        const globalConfirm = allBtns.find(b => {
                            const t = (b.innerText || '').trim();
                            return /^(Přesunout do koše|Move to trash|Move to bin)$/i.test(t) && b.offsetParent !== null;
                        });
                        if (globalConfirm) {
                            globalConfirm.click();
                            return true;
                        }
                        return false;
                    })();
                ");

                if (confirmed == "true")
                {
                    await Task.Delay(1200, ct);
                    Logger.Info($"Položka '{mediaKey}' byla úspěšně přesunuta do koše v Google Photos.");
                    return true;
                }

                await Task.Delay(500, ct);
            }

            // Fallback: Press Enter in case the confirmation button already has focus
            await DispatchEnterKeyAsync();
            await Task.Delay(1000, ct);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error($"Chyba při přesouvání položky '{mediaKey}' do koše Google Photos: {ex.Message}");
            return false;
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private async Task DispatchShiftDAsync()
    {
        try
        {
            await _webView.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent",
                @"{""type"":""keyDown"",""modifiers"":8,""windowsVirtualKeyCode"":68,""key"":""D"",""code"":""KeyD""}");
            await Task.Delay(80);
            await _webView.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent",
                @"{""type"":""keyUp"",""modifiers"":8,""windowsVirtualKeyCode"":68,""key"":""D"",""code"":""KeyD""}");
        }
        catch (Exception ex)
        {
            Logger.Warn($"Nelze odeslat zkratku Shift+D: {ex.Message}");
        }
    }

    private async Task DispatchHashKeyAsync()
    {
        try
        {
            await _webView.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent",
                @"{""type"":""char"",""text"":""#""}");
        }
        catch (Exception ex)
        {
            Logger.Warn($"Nelze odeslat zkratku '#': {ex.Message}");
        }
    }

    private async Task DispatchEnterKeyAsync()
    {
        try
        {
            await _webView.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent",
                @"{""type"":""keyDown"",""windowsVirtualKeyCode"":13,""key"":""Enter"",""code"":""Enter""}");
            await Task.Delay(60);
            await _webView.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent",
                @"{""type"":""keyUp"",""windowsVirtualKeyCode"":13,""key"":""Enter"",""code"":""Enter""}");
        }
        catch
        {
            // Ignore
        }
    }

    private async Task TriggerDownloadFromUiMenuAsync()
    {
        try
        {
            // Open 'More options' (three dots) menu and click 'Download / Stáhnout'
            await _webView.CoreWebView2.ExecuteScriptAsync(@"
                (() => {
                    const moreBtns = Array.from(document.querySelectorAll('button[aria-label*=""Další možnosti""], button[aria-label*=""More options""], div[role=""button""][aria-label*=""Další možnosti""], div[role=""button""][aria-label*=""More options""]'));
                    const visibleMore = moreBtns.find(b => b.offsetParent !== null);
                    if (visibleMore) visibleMore.click();
                })();
            ");
            await Task.Delay(500);
            await _webView.CoreWebView2.ExecuteScriptAsync(@"
                (() => {
                    const items = Array.from(document.querySelectorAll('[role=""menuitem""], button, a'));
                    const dl = items.find(el => {
                        const txt = (el.innerText || el.getAttribute('aria-label') || '').trim();
                        return /Stáhnout|Download/i.test(txt) && el.offsetParent !== null;
                    });
                    if (dl) dl.click();
                })();
            ");
        }
        catch
        {
            // Ignore
        }
    }

    private async Task<bool> NavigateAndWaitAsync(string url, int timeoutMs)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Handler(object? s, CoreWebView2NavigationCompletedEventArgs e)
        {
            tcs.TrySetResult(e.IsSuccess);
        }

        _webView.CoreWebView2.NavigationCompleted += Handler;
        try
        {
            _webView.CoreWebView2.Navigate(url);
            var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
            return completed == tcs.Task && await tcs.Task;
        }
        finally
        {
            _webView.CoreWebView2.NavigationCompleted -= Handler;
        }
    }

    private static string ExtractMediaKey(string href)
    {
        var idx = href.IndexOf("/photo/", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return string.Empty;
        var key = href[(idx + 7)..];
        var qIdx = key.IndexOfAny(['?', '#', '/']);
        if (qIdx >= 0) key = key[..qIdx];
        return key.Trim();
    }

    private static DateTime? ParseDateFromAriaLabel(string label)
    {
        if (string.IsNullOrWhiteSpace(label)) return null;

        // Try Czech date format inside aria-label: e.g. "19. 10. 2026" or "19. října 2026"
        var czNumMatch = Regex.Match(label, @"(?<d>\d{1,2})\.\s*(?<m>\d{1,2})\.\s*(?<y>20\d{2})");
        if (czNumMatch.Success &&
            int.TryParse(czNumMatch.Groups["y"].Value, out int y) &&
            int.TryParse(czNumMatch.Groups["m"].Value, out int m) &&
            int.TryParse(czNumMatch.Groups["d"].Value, out int d))
        {
            try { return new DateTime(y, m, d); } catch { }
        }

        // Try English / International formats inside aria-label: e.g. "Oct 19, 2026" or "19 Oct 2026"
        var parts = label.Split(['-', '–', '—', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        CultureInfo[] cultures = [new("cs-CZ"), CultureInfo.InvariantCulture, new("en-US"), new("en-GB")];

        for (int i = 0; i < parts.Length; i++)
        {
            var candidate = parts[i];
            if (i + 1 < parts.Length && Regex.IsMatch(parts[i + 1], @"^\d{4}"))
            {
                candidate = $"{parts[i]}, {parts[i + 1]}";
            }

            foreach (var culture in cultures)
            {
                if (DateTime.TryParse(candidate, culture, DateTimeStyles.AllowWhiteSpaces, out var parsed) &&
                    parsed.Year >= 1995 && parsed.Year <= DateTime.Now.Year + 1)
                {
                    return parsed;
                }
            }
        }

        return null;
    }

    private static string GetUniqueFilePath(string folder, string fileName)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            fileName = fileName.Replace(c, '_');
        }

        var fullPath = Path.Combine(folder, fileName);
        if (!File.Exists(fullPath)) return fullPath;

        var nameNoExt = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        int counter = 1;
        while (true)
        {
            var candidate = Path.Combine(folder, $"{nameNoExt}_{counter}{ext}");
            if (!File.Exists(candidate)) return candidate;
            counter++;
        }
    }

    private static void CleanupTempFile(string? tempPath)
    {
        if (string.IsNullOrEmpty(tempPath)) return;
        try
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
        catch
        {
            // Ignore
        }
    }

    public void Dispose()
    {
        try
        {
            _webView.Dispose();
            _hostForm.Dispose();
            _operationLock.Dispose();
        }
        catch
        {
            // Ignore
        }
    }
}
