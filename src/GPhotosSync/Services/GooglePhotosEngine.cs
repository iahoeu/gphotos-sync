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

/// <summary>
/// Speciální neaktivní okno umístěné mimo obrazovku, které ale zůstává pro systém Windows
/// a Chromium (WebView2) "viditelné" (Visible = true, Opacity = 1.0), aby Google Photos
/// nezastavovalo vykreslování virtuální mřížky fotek (IntersectionObserver / requestAnimationFrame).
/// </summary>
internal sealed class OffScreenBrowserForm : Form
{
    private bool _isUserVisible;

    public OffScreenBrowserForm()
    {
        Text = "Google Photos Sync – Prohlížeč na pozadí";
        Width = 1280;
        Height = 900;
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Location = new Point(-32000, -32000);
        Opacity = 1.0; // MUST be 1.0 so Chromium does not treat the window as transparent/occluded
    }

    protected override bool ShowWithoutActivation => !_isUserVisible;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            if (!_isUserVisible)
            {
                const int WS_EX_TOOLWINDOW = 0x00000080;
                const int WS_EX_NOACTIVATE = 0x08000000;
                cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            }
            return cp;
        }
    }

    public void MoveOffScreen()
    {
        _isUserVisible = false;
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.None;
        Location = new Point(-32000, -32000);
        Width = 1280;
        Height = 900;
        if (!Visible)
        {
            Show();
        }
    }

    public void ShowOnScreenForDiagnostics()
    {
        _isUserVisible = true;
        FormBorderStyle = FormBorderStyle.Sizable;
        ShowInTaskbar = true;
        Width = 1150;
        Height = 800;
        Location = new Point(
            Math.Max(40, (Screen.PrimaryScreen?.WorkingArea.Width ?? 1400) / 2 - 575),
            Math.Max(40, (Screen.PrimaryScreen?.WorkingArea.Height ?? 900) / 2 - 400));
        if (!Visible)
        {
            Show();
        }
        BringToFront();
        Activate();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing && _isUserVisible)
        {
            // Don't destroy the background browser when user closes the diagnostic window; move it back off-screen
            e.Cancel = true;
            MoveOffScreen();
            return;
        }
        base.OnFormClosing(e);
    }
}

public class GooglePhotosEngine : IDisposable
{
    private readonly OffScreenBrowserForm _hostForm;
    private readonly WebView2 _webView;
    private CoreWebView2Environment? _env;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private bool _isInitialized;
    private bool _isKnownLoggedIn;

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".m4v", ".avi", ".mkv", ".3gp", ".webm", ".mts", ".m2ts"
    };

    private static readonly Dictionary<string, int> CzechMonths = new(StringComparer.OrdinalIgnoreCase)
    {
        ["leden"] = 1, ["ledna"] = 1, ["led"] = 1,
        ["únor"] = 2, ["února"] = 2, ["unor"] = 2, ["unora"] = 2, ["úno"] = 2,
        ["březen"] = 3, ["března"] = 3, ["brezen"] = 3, ["brezna"] = 3, ["bře"] = 3,
        ["duben"] = 4, ["dubna"] = 4, ["dub"] = 4,
        ["květen"] = 5, ["května"] = 5, ["kveten"] = 5, ["kvetna"] = 5, ["kvě"] = 5,
        ["červen"] = 6, ["června"] = 6, ["cerven"] = 6, ["cervna"] = 6, ["čvn"] = 6,
        ["červenec"] = 7, ["července"] = 7, ["cervenec"] = 7, ["cervence"] = 7, ["čvc"] = 7,
        ["srpen"] = 8, ["srpna"] = 8, ["srp"] = 8,
        ["září"] = 9, ["zari"] = 9, ["zář"] = 9,
        ["říjen"] = 10, ["října"] = 10, ["rijen"] = 10, ["rijna"] = 10, ["říj"] = 10,
        ["listopad"] = 11, ["listopadu"] = 11, ["lis"] = 11,
        ["prosinec"] = 12, ["prosince"] = 12, ["pro"] = 12
    };

    public bool IsKnownLoggedIn
    {
        get => _isKnownLoggedIn;
        set => _isKnownLoggedIn = value;
    }

    public GooglePhotosEngine()
    {
        _hostForm = new OffScreenBrowserForm();
        _webView = new WebView2
        {
            Dock = DockStyle.Fill
        };
        _hostForm.Controls.Add(_webView);

        // Force handle creation and keep form visible off-screen so WebView2 never suspends rendering
        var _ = _hostForm.Handle;
        _hostForm.MoveOffScreen();
    }

    public void ShowDiagnosticBrowserWindow()
    {
        _hostForm.ShowOnScreenForDiagnostics();
    }

    public async Task EnsureInitializedAsync()
    {
        if (_isInitialized && _webView.CoreWebView2 != null)
            return;

        Directory.CreateDirectory(AppConfig.WebViewProfileFolder);

        // Critical Chromium flags so WebView2 renders virtualized grids (Google Photos) even when off-screen or in a VM
        var options = new CoreWebView2EnvironmentOptions(
            "--disable-background-timer-throttling " +
            "--disable-backgrounding-occluded-windows " +
            "--disable-renderer-backgrounding " +
            "--disable-features=CalculateNativeWinOcclusion,IntensiveWakeUpThrottling");

        _env = await CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null,
            userDataFolder: AppConfig.WebViewProfileFolder,
            options: options);

        if (!_hostForm.Visible)
        {
            _hostForm.MoveOffScreen();
        }

        await _webView.EnsureCoreWebView2Async(_env);
        _webView.CoreWebView2!.Settings.IsStatusBarEnabled = false;
        _webView.CoreWebView2.Settings.AreDefaultScriptDialogsEnabled = false;
        _webView.CoreWebView2.IsMuted = true;

        // Force document.visibilityState to always report 'visible' so Google Photos lazy loader stays active
        await _webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(@"
            try {
                Object.defineProperty(document, 'visibilityState', { get: () => 'visible', configurable: true });
                Object.defineProperty(document, 'hidden', { get: () => false, configurable: true });
                Object.defineProperty(document, 'webkitVisibilityState', { get: () => 'visible', configurable: true });
                Object.defineProperty(document, 'webkitHidden', { get: () => false, configurable: true });
            } catch (e) {}
        ");

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
            await NavigateAndWaitForDomAsync("https://photos.google.com/", 12000);

            for (int attempt = 0; attempt < 6; attempt++)
            {
                await Task.Delay(700);
                var url = _webView.CoreWebView2.Source ?? "";
                if (url.Contains("accounts.google.com", StringComparison.OrdinalIgnoreCase) ||
                    url.Contains("/about", StringComparison.OrdinalIgnoreCase) ||
                    url.Contains("workspace.google.com", StringComparison.OrdinalIgnoreCase))
                {
                    _isKnownLoggedIn = false;
                    return false;
                }

                var checkJs = @"
                    (() => {
                        const hasPhotos = document.querySelectorAll('a[href*=""/photo/""], a[href*=""photo/""]').length > 0;
                        const hasAppShell = document.querySelector('c-wiz') !== null &&
                                            (document.querySelector('input[aria-label]') !== null ||
                                             document.querySelector('a[aria-label*=""Google""]') !== null ||
                                             document.querySelector('img[src*=""googleusercontent""]') !== null ||
                                             document.querySelector('[role=""main""]') !== null);
                        const hasSignInBtn = Array.from(document.querySelectorAll('a, button'))
                            .some(el => /^(Přihlásit se|Sign in|Přejít do Fotek Google|Go to Google Photos)$/i.test((el.innerText || '').trim()));
                        if (hasSignInBtn && !hasPhotos) return false;
                        return hasPhotos || hasAppShell;
                    })();
                ";

                var result = await _webView.CoreWebView2.ExecuteScriptAsync(checkJs);
                if (result == "true")
                {
                    _isKnownLoggedIn = true;
                    return true;
                }
            }

            return _isKnownLoggedIn;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Kontrola přihlášení selhala: {ex.Message}");
            return _isKnownLoggedIn;
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

            Logger.Info("Otevírám časovou osu Google Photos (https://photos.google.com/)...");
            await NavigateAndWaitForDomAsync("https://photos.google.com/", 18000);

            // Trigger resize & scroll wakeup so Google Photos virtualized grid mounts items immediately
            await _webView.CoreWebView2.ExecuteScriptAsync(@"
                (() => {
                    window.dispatchEvent(new Event('resize'));
                    window.dispatchEvent(new Event('focus'));
                    document.dispatchEvent(new Event('visibilitychange'));
                })();
            ");

            // Wait up to 10 seconds for timeline photo elements to mount in the DOM
            int domAnchorCount = 0;
            for (int wait = 0; wait < 15; wait++)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(600, ct);

                var countJson = await _webView.CoreWebView2.ExecuteScriptAsync(@"
                    (() => {
                        return document.querySelectorAll('a[href*=""/photo/""], a[href*=""photo/""]').length;
                    })();
                ");

                if (int.TryParse(countJson, out domAnchorCount) && domAnchorCount > 0)
                {
                    break;
                }

                // Nudge scroll slightly to wake up IntersectionObserver if needed
                if (wait == 4 || wait == 8)
                {
                    await _webView.CoreWebView2.ExecuteScriptAsync("window.scrollBy(0, 120); window.scrollBy(0, -120);");
                }
            }

            var currentUrl = _webView.CoreWebView2.Source ?? "";
            Logger.Info($"Stránka Google Photos načtena ({currentUrl}), počáteční počet DOM odkazů: {domAnchorCount}.");

            for (int step = 0; step < maxScrollSteps; step++)
            {
                ct.ThrowIfCancellationRequested();

                // Extract from both rendered DOM anchors (including Shadow DOM / c-wiz) AND AF_initDataCallback scripts
                var extractScript = @"
                    (() => {
                        const results = [];
                        const seen = new Set();

                        function addEntry(hrefOrKey, label, isVid, dateHint) {
                            let key = '';
                            const idx = hrefOrKey.indexOf('/photo/');
                            if (idx >= 0) {
                                key = hrefOrKey.substring(idx + 7).split(/[?#/]/)[0].trim();
                            } else if (/^AF1Qip[A-Za-z0-9_-]{20,}$/.test(hrefOrKey)) {
                                key = hrefOrKey;
                            }
                            if (!key || key.length < 15 || seen.has(key)) return;
                            seen.add(key);
                            results.push({
                                key: key,
                                label: label || '',
                                isVid: !!isVid,
                                dateHint: dateHint || ''
                            });
                        }

                        // 1. Collect all <a> elements containing /photo/ (including shadow roots)
                        function queryAllDeep(root) {
                            const anchors = root.querySelectorAll ? Array.from(root.querySelectorAll('a[href*=""photo/""]')) : [];
                            for (const a of anchors) {
                                const href = a.getAttribute('href') || a.href || '';
                                // Ignore album or trash sub-links if they aren't direct timeline photos, except /photo/
                                if (href.includes('/album/') && !href.includes('/photo/')) continue;

                                let label = a.getAttribute('aria-label') || '';
                                if (!label) {
                                    const img = a.querySelector('img[alt], [aria-label]');
                                    if (img) label = img.getAttribute('aria-label') || img.getAttribute('alt') || '';
                                }

                                // Find nearest section date header (e.g. 'Dnes', 'Včera', 'so 17. 10.')
                                let dateHint = '';
                                let parent = a.parentElement;
                                for (let depth = 0; depth < 7 && parent; depth++) {
                                    const prev = parent.previousElementSibling;
                                    if (prev) {
                                        const hTxt = (prev.innerText || '').trim().split('\n')[0];
                                        if (hTxt && hTxt.length < 40) {
                                            dateHint = hTxt;
                                            break;
                                        }
                                    }
                                    parent = parent.parentElement;
                                }

                                const isVid = /video|videa|přehrát|play/i.test(label) ||
                                              a.querySelector('[aria-label*=""Video""], [aria-label*=""video""], [data-is-video=""true""]') !== null;

                                addEntry(href, label, isVid, dateHint);
                            }

                            const allEls = root.querySelectorAll ? root.querySelectorAll('*') : [];
                            for (const el of allEls) {
                                if (el.shadowRoot) {
                                    queryAllDeep(el.shadowRoot);
                                }
                            }
                        }

                        queryAllDeep(document);

                        // 2. Fallback: parse AF_initDataCallback script blocks if DOM virtualization hid any items
                        if (results.length === 0) {
                            const scripts = Array.from(document.querySelectorAll('script'));
                            const keyRegex = /""(AF1Qip[A-Za-z0-9_-]{22,80})""/g;
                            for (const s of scripts) {
                                const txt = s.textContent || '';
                                if (!txt.includes('AF1Qip')) continue;
                                let m;
                                while ((m = keyRegex.exec(txt)) !== null) {
                                    addEntry(m[1], '', false, '');
                                }
                            }
                        }

                        return JSON.stringify(results);
                    })();
                ";

                var rawJson = await _webView.CoreWebView2.ExecuteScriptAsync(extractScript);
                if (!string.IsNullOrWhiteSpace(rawJson) && rawJson != "null")
                {
                    // ExecuteScriptAsync returns a JSON-encoded string when the script returns JSON.stringify(...)
                    var innerJson = JsonSerializer.Deserialize<string>(rawJson);
                    if (!string.IsNullOrWhiteSpace(innerJson))
                    {
                        using var doc = JsonDocument.Parse(innerJson);
                        if (doc.RootElement.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var elem in doc.RootElement.EnumerateArray())
                            {
                                var key = elem.GetProperty("key").GetString() ?? "";
                                var label = elem.GetProperty("label").GetString() ?? "";
                                var dateHint = elem.TryGetProperty("dateHint", out var dh) ? dh.GetString() ?? "" : "";
                                var isVid = elem.GetProperty("isVid").GetBoolean();

                                if (string.IsNullOrEmpty(key) || itemsById.ContainsKey(key))
                                    continue;

                                var estimatedDate = ParseDateFromAriaLabel(label) ?? ParseDateFromAriaLabel(dateHint);

                                itemsById[key] = new DiscoveredMediaItem
                                {
                                    MediaKey = key,
                                    PhotoUrl = $"https://photos.google.com/photo/{key}",
                                    AriaLabel = !string.IsNullOrWhiteSpace(label) ? label : dateHint,
                                    IsVideo = isVid,
                                    EstimatedDate = estimatedDate
                                };
                            }
                        }
                    }
                }

                Logger.Info($"Skenování časové osy (krok {step + 1}/{maxScrollSteps}): doposud nalezeno {itemsById.Count} položek.");

                if (step < maxScrollSteps - 1)
                {
                    // Scroll both window and any scrollable containers + send mouse wheel event
                    await _webView.CoreWebView2.ExecuteScriptAsync(@"
                        (() => {
                            window.scrollBy(0, 1200);
                            if (document.scrollingElement) document.scrollingElement.scrollTop += 1200;
                            const all = Array.from(document.querySelectorAll('c-wiz, main, [role=""main""], div'));
                            for (const el of all) {
                                if (el.scrollHeight > el.clientHeight + 80 && el.clientHeight > 200) {
                                    const style = window.getComputedStyle(el);
                                    if (style.overflowY === 'auto' || style.overflowY === 'scroll' || el.tagName === 'C-WIZ') {
                                        el.scrollTop += 1200;
                                    }
                                }
                            }
                        })();
                    ");

                    try
                    {
                        await _webView.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent",
                            @"{""type"":""mouseWheel"",""x"":640,""y"":450,""deltaX"":0,""deltaY"":1200}");
                    }
                    catch { }

                    await Task.Delay(1000, ct);
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
                        originalFileName = $"{item.MediaKey[..Math.Min(12, item.MediaKey.Length)]}.jpg";
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
                Logger.Info($"Otevírám detail položky pro stažení: {item.PhotoUrl}");
                await NavigateAndWaitForDomAsync(item.PhotoUrl, 15000);
                await Task.Delay(1500, ct);

                // Method 1: Trigger Shift+D via CDP + JS KeyboardEvent
                await DispatchShiftDAsync();

                var startCheck1 = await Task.WhenAny(tcsDownload.Task, Task.Delay(3000, ct));
                if (startCheck1 != tcsDownload.Task && tempFilePath == null)
                {
                    // Method 2: Click 'More options' (three dots) -> 'Download / Stáhnout' in Google Photos UI
                    Logger.Info($"Zkouším stahování přes menu 'Další možnosti -> Stáhnout' pro {item.MediaKey}...");
                    await TriggerDownloadFromUiMenuAsync();
                }

                var startCheck2 = await Task.WhenAny(tcsDownload.Task, Task.Delay(3500, ct));
                if (startCheck2 != tcsDownload.Task && tempFilePath == null)
                {
                    // Method 3: Direct googleusercontent.com '=d' original download link extraction
                    Logger.Info($"Zkouším přímý stahovací odkaz (=d) pro {item.MediaKey}...");
                    await TriggerDirectGoogleUserContentDownloadAsync();
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

            await NavigateAndWaitForDomAsync(url, 15000);
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
                        const btns = Array.from(document.querySelectorAll(sel))
                            .filter(b => b.offsetParent !== null && b.getBoundingClientRect().width > 0);
                        if (btns.length > 0) {
                            btns[btns.length - 1].click();
                            return true;
                        }
                    }
                    return false;
                })();
            ");

            if (clickedToolbarTrash != "true")
            {
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
                @"{""type"":""rawKeyDown"",""modifiers"":8,""windowsVirtualKeyCode"":68,""nativeVirtualKeyCode"":68,""key"":""D"",""code"":""KeyD""}");
            await _webView.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent",
                @"{""type"":""char"",""modifiers"":8,""windowsVirtualKeyCode"":68,""key"":""D"",""text"":""D"",""unmodifiedText"":""d""}");
            await Task.Delay(60);
            await _webView.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent",
                @"{""type"":""keyUp"",""modifiers"":8,""windowsVirtualKeyCode"":68,""nativeVirtualKeyCode"":68,""key"":""D"",""code"":""KeyD""}");

            // Also dispatch synthetic JS keyboard event in case focus is inside a custom element
            await _webView.CoreWebView2.ExecuteScriptAsync(@"
                (() => {
                    const target = document.activeElement || document.body;
                    for (const type of ['keydown', 'keypress', 'keyup']) {
                        target.dispatchEvent(new KeyboardEvent(type, {
                            key: 'D', code: 'KeyD', keyCode: 68, which: 68, shiftKey: true, bubbles: true, cancelable: true
                        }));
                    }
                })();
            ");
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
            // Pick the LAST visible 'More options' button in the DOM (which belongs to the active photo overlay)
            await _webView.CoreWebView2.ExecuteScriptAsync(@"
                (() => {
                    const moreBtns = Array.from(document.querySelectorAll(
                        'button[aria-label*=""Další možnosti""], button[aria-label*=""More options""], ' +
                        'div[role=""button""][aria-label*=""Další možnosti""], div[role=""button""][aria-label*=""More options""]'
                    )).filter(b => b.offsetParent !== null && b.getBoundingClientRect().width > 0);

                    if (moreBtns.length > 0) {
                        moreBtns[moreBtns.length - 1].click();
                    }
                })();
            ");
            await Task.Delay(600);
            await _webView.CoreWebView2.ExecuteScriptAsync(@"
                (() => {
                    const items = Array.from(document.querySelectorAll('[role=""menuitem""], [role=""option""], li, button, a'))
                        .filter(el => el.offsetParent !== null && el.getBoundingClientRect().width > 0);
                    const dl = items.find(el => {
                        const txt = (el.innerText || el.getAttribute('aria-label') || '').trim();
                        return /^(Stáhnout|Download)(\b|\s|Shift)/i.test(txt) || /Stáhnout originál|Download original/i.test(txt);
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

    private async Task TriggerDirectGoogleUserContentDownloadAsync()
    {
        try
        {
            // Find the main high-res photo/video URL on the photo detail page and append '=d' to force original file download
            var directUrlJson = await _webView.CoreWebView2.ExecuteScriptAsync(@"
                (() => {
                    const imgs = Array.from(document.querySelectorAll('img[src*=""googleusercontent.com""]'))
                        .filter(img => img.src && !img.src.includes('/a/') && !img.src.includes('=s'))
                        .sort((a, b) => (b.naturalWidth * b.naturalHeight || b.clientWidth * b.clientHeight) - (a.naturalWidth * a.naturalHeight || a.clientWidth * a.clientHeight));

                    if (imgs.length > 0) {
                        let baseUrl = imgs[0].src.split('=')[0];
                        return baseUrl + '=d';
                    }
                    return '';
                })();
            ");

            var directUrl = JsonSerializer.Deserialize<string>(directUrlJson ?? "\"\"");
            if (!string.IsNullOrWhiteSpace(directUrl) && directUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                await _webView.CoreWebView2.ExecuteScriptAsync($@"
                    (() => {{
                        const a = document.createElement('a');
                        a.href = {JsonSerializer.Serialize(directUrl)};
                        a.download = '';
                        document.body.appendChild(a);
                        a.click();
                        a.remove();
                    }})();
                ");
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"Nelze spustit přímé stažení (=d): {ex.Message}");
        }
    }

    private async Task NavigateAndWaitForDomAsync(string url, int timeoutMs)
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
            await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
        }
        catch (Exception ex)
        {
            Logger.Warn($"Navigace na '{url}' hlásí varování: {ex.Message}");
        }
        finally
        {
            _webView.CoreWebView2.NavigationCompleted -= Handler;
        }
    }

    private static DateTime? ParseDateFromAriaLabel(string label)
    {
        if (string.IsNullOrWhiteSpace(label)) return null;

        var trimmed = label.Trim();
        if (Regex.IsMatch(trimmed, @"\b(dnes|today)\b", RegexOptions.IgnoreCase))
            return DateTime.Today;
        if (Regex.IsMatch(trimmed, @"\b(včera|vcera|yesterday)\b", RegexOptions.IgnoreCase))
            return DateTime.Today.AddDays(-1);

        // 1. Try numeric Czech format: "19. 10. 2026" or "19. 10."
        var czNumMatch = Regex.Match(trimmed, @"(?<d>\d{1,2})\.\s*(?<m>\d{1,2})\.(\s*(?<y>20\d{2}))?");
        if (czNumMatch.Success &&
            int.TryParse(czNumMatch.Groups["m"].Value, out int mNum) && mNum is >= 1 and <= 12 &&
            int.TryParse(czNumMatch.Groups["d"].Value, out int dNum) && dNum is >= 1 and <= 31)
        {
            int yNum = czNumMatch.Groups["y"].Success && int.TryParse(czNumMatch.Groups["y"].Value, out int parsedY)
                ? parsedY
                : DateTime.Today.Year;
            try { return new DateTime(yNum, mNum, dNum); } catch { }
        }

        // 2. Try Czech word month format: "19. října 2026" or "19. října"
        var czWordMatch = Regex.Match(trimmed, @"(?<d>\d{1,2})\.\s*(?<month>[a-zA-ZáčďéěíňóřšťúůýžÁČĎÉĚÍŇÓŘŠŤÚŮÝŽ]+)(\s+(?<y>20\d{2}))?");
        if (czWordMatch.Success &&
            int.TryParse(czWordMatch.Groups["d"].Value, out int dWord) && dWord is >= 1 and <= 31)
        {
            var mName = czWordMatch.Groups["month"].Value;
            if (CzechMonths.TryGetValue(mName, out int mWord))
            {
                int yWord = czWordMatch.Groups["y"].Success && int.TryParse(czWordMatch.Groups["y"].Value, out int parsedY2)
                    ? parsedY2
                    : DateTime.Today.Year;
                try { return new DateTime(yWord, mWord, dWord); } catch { }
            }
        }

        // 3. Try English / International formats inside aria-label: e.g. "Oct 19, 2026" or "19 Oct 2026"
        var parts = trimmed.Split(['-', '–', '—', ',', ':'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
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
