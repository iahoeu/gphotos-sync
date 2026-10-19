using System.ComponentModel;
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
    public bool DiscoveredFromRecentlyAdded { get; set; }

    /// <summary>
    /// Stáhnout bez ohledu na časový limit (např. fotky vrácené po zrušení hromadného mazání).
    /// </summary>
    public bool ForceDownload { get; set; }
}

/// <summary>
/// Výsledek pokusu o přesun do koše. NotAttempted = na koš se vůbec nekliklo (lze bezpečně opakovat);
/// Unconfirmed = kliknuto, ale výsledek nejde ověřit (NEOPAKOVAT – mohlo by to znovu smazat fotku obnovenou z koše).
/// </summary>
public enum TrashOutcome
{
    Trashed,
    NotAttempted,
    Unconfirmed
}

public enum TimelineScanDecision
{
    Continue,
    KnownBoundaryReached,
    PossibleEnd
}

/// <summary>
/// Čistá (testovatelná) logika rozhodování při posouvání časové osy: kolik kroků zbývá, kdy je dosažena
/// vrstva již známých položek a kdy může jít o konec knihovny. Kroky potřebné k dosažení místa, kde skončil
/// minulý sken, se nepočítají do limitu dávky (mají vlastní strop).
/// </summary>
public sealed class TimelineScanProgress
{
    public const int DefaultMaxResumeSeekSteps = 600;

    private readonly int _maxScanSteps;
    private readonly int _maxResumeSeekSteps;
    private readonly bool _scanAllHistory;
    private readonly int _minStepsBeforeEarlyStop;
    private int _consecutiveStepsWithoutNewItems;
    private int _consecutiveStepsAllKnownOrOlder;

    public TimelineScanProgress(int maxScanSteps, bool scanAllHistory, bool hasResumeKey, int maxResumeSeekSteps = DefaultMaxResumeSeekSteps)
    {
        _maxScanSteps = Math.Max(1, maxScanSteps);
        _maxResumeSeekSteps = Math.Max(0, maxResumeSeekSteps);
        _scanAllHistory = scanAllHistory;
        _minStepsBeforeEarlyStop = scanAllHistory ? 10 : 3;
        PassedResumeKey = !hasResumeKey;
    }

    public bool PassedResumeKey { get; private set; }
    public int ScanSteps { get; private set; }
    public int TotalSteps { get; private set; }

    public bool CanContinue => ScanSteps < _maxScanSteps && TotalSteps < _maxScanSteps + _maxResumeSeekSteps;
    public bool ShouldScroll => CanContinue;
    public bool ShouldGiveUpWithoutConfirmedEnd => _consecutiveStepsWithoutNewItems >= 8;

    public void MarkResumeKeyReached() => PassedResumeKey = true;

    public TimelineScanDecision RecordStep(int newlyAddedItems, int newUnseenCandidates)
    {
        TotalSteps++;
        if (PassedResumeKey)
            ScanSteps++;

        if (newlyAddedItems == 0)
        {
            _consecutiveStepsWithoutNewItems++;
        }
        else
        {
            _consecutiveStepsWithoutNewItems = 0;
            if (!_scanAllHistory && PassedResumeKey && newUnseenCandidates == 0)
                _consecutiveStepsAllKnownOrOlder++;
            else
                _consecutiveStepsAllKnownOrOlder = 0;
        }

        if (!_scanAllHistory && PassedResumeKey && ScanSteps >= _minStepsBeforeEarlyStop && _consecutiveStepsAllKnownOrOlder >= 2)
            return TimelineScanDecision.KnownBoundaryReached;

        if (TotalSteps >= _minStepsBeforeEarlyStop && _consecutiveStepsWithoutNewItems >= 4)
            return TimelineScanDecision.PossibleEnd;

        return TimelineScanDecision.Continue;
    }
}

public class TimelineScanResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public List<DiscoveredMediaItem> Items { get; set; } = [];
    public bool ReachedTargetBoundary { get; set; } = false;
    public string? LastScannedMediaKey { get; set; }
    public DateTime? OldestScannedDate { get; set; }

    /// <summary>
    /// Místo, kde skončil minulý sken, nebylo v limitu posunu dosaženo -> hlubší část knihovny se nekontroluje.
    /// </summary>
    public bool ResumeKeyNotReached { get; set; }
}

public class AccountCheckResult
{
    public bool IsLoggedIn { get; set; }
    public bool IsAccountVerifiedForDelete { get; set; }
    public string? DetectedEmail { get; set; }
    public string? ErrorMessage { get; set; }
    public string? DeleteBlockedReason { get; set; }
}

public class DownloadResult
{
    public bool Success { get; set; }
    public bool SkippedVideo { get; set; }
    public string LocalFileName { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public string FileHash { get; set; } = string.Empty;
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
    private string? _lastVerifiedEmail;
    private string? _manuallyConfirmedSessionEmail;

    public string? LastVerifiedEmail => _lastVerifiedEmail;

    /// <summary>
    /// Bod 8 (Doplněk): Uloží ruční potvrzení e-mailu účtu Google uživatelem pro aktuální přihlášenou relaci,
    /// pokud Google Photos e-mail v DOMu přímo nevystavuje. Při odhlášení nebo změně relace se automaticky ruší.
    /// </summary>
    public void ConfirmCurrentSessionAccountEmail(string confirmedEmail)
    {
        if (!string.IsNullOrWhiteSpace(confirmedEmail))
        {
            _manuallyConfirmedSessionEmail = confirmedEmail.Trim();
            _lastVerifiedEmail = _manuallyConfirmedSessionEmail;
        }
    }

    public void ClearConfirmedSessionAccountEmail()
    {
        _manuallyConfirmedSessionEmail = null;
        Interlocked.Increment(ref _accountSessionVersion);
    }

    /// <summary>
    /// Zvyšuje se pokaždé, když mohl uživatel v prohlížeči změnit účet (přihlašovací okno, zobrazený prohlížeč).
    /// Rozběhnutá dávka mazání se při změně zastaví.
    /// </summary>
    public int AccountSessionVersion => Volatile.Read(ref _accountSessionVersion);
    private int _accountSessionVersion;

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".m4v", ".avi", ".mkv", ".3gp", ".webm", ".mts", ".m2ts"
    };

    private static readonly Dictionary<string, int> CzechMonths = new(StringComparer.OrdinalIgnoreCase)
    {
        ["leden"] = 1, ["ledna"] = 1, ["led"] = 1, ["jan"] = 1, ["january"] = 1,
        ["únor"] = 2, ["února"] = 2, ["unor"] = 2, ["unora"] = 2, ["úno"] = 2, ["feb"] = 2, ["february"] = 2,
        ["březen"] = 3, ["března"] = 3, ["brezen"] = 3, ["brezna"] = 3, ["bře"] = 3, ["mar"] = 3, ["march"] = 3,
        ["duben"] = 4, ["dubna"] = 4, ["dub"] = 4, ["apr"] = 4, ["april"] = 4,
        ["květen"] = 5, ["května"] = 5, ["kveten"] = 5, ["kvetna"] = 5, ["kvě"] = 5, ["may"] = 5,
        ["červen"] = 6, ["června"] = 6, ["cerven"] = 6, ["cervna"] = 6, ["čvn"] = 6, ["jun"] = 6, ["june"] = 6,
        ["červenec"] = 7, ["července"] = 7, ["cervenec"] = 7, ["cervence"] = 7, ["čvc"] = 7, ["jul"] = 7, ["july"] = 7,
        ["srpen"] = 8, ["srpna"] = 8, ["srp"] = 8, ["aug"] = 8, ["august"] = 8,
        ["září"] = 9, ["zari"] = 9, ["zář"] = 9, ["sep"] = 9, ["sept"] = 9, ["september"] = 9,
        ["říjen"] = 10, ["října"] = 10, ["rijen"] = 10, ["rijna"] = 10, ["říj"] = 10, ["oct"] = 10, ["october"] = 10,
        ["listopad"] = 11, ["listopadu"] = 11, ["lis"] = 11, ["nov"] = 11, ["november"] = 11,
        ["prosinec"] = 12, ["prosince"] = 12, ["pro"] = 12, ["dec"] = 12, ["december"] = 12
    };

    public bool IsKnownLoggedIn
    {
        get => _isKnownLoggedIn;
        set => _isKnownLoggedIn = value;
    }

    /// <summary>
    /// P3-16: Synchronizační objekt UI vlákna pro FileSystemWatcher a časovače.
    /// </summary>
    public ISynchronizeInvoke UiSyncObject => _hostForm;

    public void InvokeOnUiThread(Action action)
    {
        if (_hostForm.IsDisposed) return;
        if (_hostForm.InvokeRequired)
        {
            try { _hostForm.BeginInvoke(action); } catch { }
        }
        else
        {
            action();
        }
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
        // V zobrazeném prohlížeči lze přepnout účet -> dřívější ruční potvrzení e-mailu už neplatí
        _manuallyConfirmedSessionEmail = null;
        Interlocked.Increment(ref _accountSessionVersion);
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

    public async Task<bool> CheckLoggedInAsync(string? expectedEmail = null)
    {
        var res = await CheckLoginAndAccountAsync(expectedEmail);
        return res.IsLoggedIn;
    }

    /// <summary>
    /// P1-8 & Bod 8 (Doplněk): Přísná kontrola přihlášení a shody Google účtu.
    /// - Při výjimce nebo neprůkazném výsledku VŽDY vrací IsLoggedIn = false.
    /// - Mazání (IsAccountVerifiedForDelete) je povolené VÝHRADNĚ tehdy, když je vyplněn ExpectedGoogleAccountEmail
    ///   a podařilo se jej ověřit ze stránky (nebo jej uživatel pro tuto aktivní relaci výslovně potvrdil).
    /// </summary>
    public async Task<AccountCheckResult> CheckLoginAndAccountAsync(string? expectedEmail = null)
    {
        await _operationLock.WaitAsync();
        try
        {
            await EnsureInitializedAsync();
            bool navOk = await NavigateAndWaitForDomAsync("https://photos.google.com/", 12000);
            if (!navOk)
            {
                _isKnownLoggedIn = false;
                _manuallyConfirmedSessionEmail = null;
                return new AccountCheckResult
                {
                    IsLoggedIn = false,
                    IsAccountVerifiedForDelete = false,
                    ErrorMessage = "Nepodařilo se načíst stránku Google Photos (navigace selhala nebo vypršel časový limit)."
                };
            }

            for (int attempt = 0; attempt < 6; attempt++)
            {
                await Task.Delay(700);
                var url = _webView.CoreWebView2.Source ?? "";
                if (IsLoginOrMarketingUrl(url))
                {
                    _isKnownLoggedIn = false;
                    _manuallyConfirmedSessionEmail = null;
                    return new AccountCheckResult
                    {
                        IsLoggedIn = false,
                        IsAccountVerifiedForDelete = false,
                        ErrorMessage = "Uživatel není přihlášen ke Google Photos."
                    };
                }

                var checkJs = @"
                    (() => {
                        const hasPhotos = document.querySelectorAll('a[href*=""/photo/""], a[href*=""photo/""]').length > 0;
                        const hasAppShell = document.querySelector('c-wiz') !== null &&
                                            (document.querySelector('input[aria-label]') !== null ||
                                             document.querySelector('a[aria-label*=""Google""]') !== null ||
                                             document.querySelector('button[aria-label*=""Google""]') !== null ||
                                             document.querySelector('img[src*=""googleusercontent""]') !== null ||
                                             document.querySelector('[role=""main""]') !== null);
                        const hasSignInBtn = Array.from(document.querySelectorAll('a, button'))
                            .some(el => /^(Přihlásit se|Sign in|Přejít do Fotek Google|Go to Google Photos)$/i.test((el.innerText || '').trim()));
                        if (hasSignInBtn && !hasPhotos) {
                            return JSON.stringify({ loggedIn: false, email: '' });
                        }

                        let email = '';
                        const emailRegex = /([a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,})/;
                        const accountEls = Array.from(document.querySelectorAll('a[aria-label*=""@""], button[aria-label*=""@""], div[aria-label*=""@""], [data-email]'));
                        for (const el of accountEls) {
                            const dataEmail = el.getAttribute('data-email') || '';
                            if (emailRegex.test(dataEmail)) {
                                email = dataEmail.match(emailRegex)[1];
                                break;
                            }
                            const aria = el.getAttribute('aria-label') || '';
                            const m = aria.match(emailRegex);
                            if (m) {
                                email = m[1];
                                break;
                            }
                        }

                        if (!email) {
                            const scripts = Array.from(document.querySelectorAll('script'));
                            for (const s of scripts) {
                                const txt = s.textContent || '';
                                if (txt.includes('WIZ_global_data') || txt.includes('AF_initDataCallback')) {
                                    const m = txt.match(/""([a-zA-Z0-9._%+-]+@(?:gmail\.com|googlemail\.com|[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}))""/);
                                    if (m) {
                                        email = m[1];
                                        break;
                                    }
                                }
                            }
                        }

                        return JSON.stringify({
                            loggedIn: !!(hasPhotos || hasAppShell),
                            email: email || ''
                        });
                    })();
                ";

                var rawJson = await _webView.CoreWebView2.ExecuteScriptAsync(checkJs);
                if (!string.IsNullOrWhiteSpace(rawJson) && rawJson != "null")
                {
                    var innerJson = JsonSerializer.Deserialize<string>(rawJson);
                    if (!string.IsNullOrWhiteSpace(innerJson))
                    {
                        using var doc = JsonDocument.Parse(innerJson);
                        bool isLogged = doc.RootElement.GetProperty("loggedIn").GetBoolean();
                        string email = doc.RootElement.TryGetProperty("email", out var em) ? (em.GetString() ?? "").Trim() : "";

                        if (isLogged)
                        {
                            var cleanExpected = expectedEmail?.Trim() ?? "";

                            if (!string.IsNullOrWhiteSpace(email))
                            {
                                _lastVerifiedEmail = email;

                                if (!string.IsNullOrWhiteSpace(cleanExpected) &&
                                    !string.Equals(cleanExpected, email, StringComparison.OrdinalIgnoreCase))
                                {
                                    _isKnownLoggedIn = false;
                                    _manuallyConfirmedSessionEmail = null;
                                    var mismatchMsg = $"Přihlášený účet Google ({email}) neodpovídá dříve nastavenému účtu ({cleanExpected}). Synchronizace a mazání byly zastaveny.";
                                    Logger.Error(mismatchMsg);
                                    return new AccountCheckResult
                                    {
                                        IsLoggedIn = false,
                                        IsAccountVerifiedForDelete = false,
                                        DetectedEmail = email,
                                        ErrorMessage = mismatchMsg
                                    };
                                }

                                _isKnownLoggedIn = true;
                                bool verifiedForDelete = !string.IsNullOrWhiteSpace(cleanExpected) &&
                                                         string.Equals(cleanExpected, email, StringComparison.OrdinalIgnoreCase);

                                return new AccountCheckResult
                                {
                                    IsLoggedIn = true,
                                    IsAccountVerifiedForDelete = verifiedForDelete,
                                    DetectedEmail = email,
                                    DeleteBlockedReason = verifiedForDelete ? null : "E-mail účtu Google zatím nebyl uložen v konfiguraci."
                                };
                            }

                            // E-mail se ze stránky nepodařilo vyčíst (Bod 8 + Doplněk):
                            _isKnownLoggedIn = true;

                            bool isManuallyConfirmedForExpected =
                                !string.IsNullOrWhiteSpace(cleanExpected) &&
                                !string.IsNullOrWhiteSpace(_manuallyConfirmedSessionEmail) &&
                                string.Equals(cleanExpected, _manuallyConfirmedSessionEmail, StringComparison.OrdinalIgnoreCase);

                            return new AccountCheckResult
                            {
                                IsLoggedIn = true,
                                IsAccountVerifiedForDelete = isManuallyConfirmedForExpected,
                                DetectedEmail = isManuallyConfirmedForExpected ? cleanExpected : null,
                                DeleteBlockedReason = isManuallyConfirmedForExpected
                                    ? null
                                    : string.IsNullOrWhiteSpace(cleanExpected)
                                        ? "Mazání v cloudu je vypnuté: není nastaven a potvrzen e-mail účtu Google (potvrďte účet v menu u ikony v liště)."
                                        : $"Mazání v cloudu je pozastaveno: ze stránky Google Photos se nepodařilo automaticky ověřit e-mail účtu '{cleanExpected}'. Potvrďte účet v menu u ikony v liště."
                            };
                        }
                    }
                }
            }

            _isKnownLoggedIn = false;
            _manuallyConfirmedSessionEmail = null;
            return new AccountCheckResult
            {
                IsLoggedIn = false,
                IsAccountVerifiedForDelete = false,
                ErrorMessage = "Nepodařilo se průkazně ověřit přihlášení ke Google Photos."
            };
        }
        catch (Exception ex)
        {
            Logger.Warn($"Kontrola přihlášení selhala: {ex.Message}");
            _isKnownLoggedIn = false;
            _manuallyConfirmedSessionEmail = null;
            return new AccountCheckResult
            {
                IsLoggedIn = false,
                IsAccountVerifiedForDelete = false,
                ErrorMessage = $"Chyba při ověřování přihlášení: {ex.Message}"
            };
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private static bool IsLoginOrMarketingUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return true;
        return url.Contains("accounts.google.com", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("/about", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("/signin", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("/ServiceLogin", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("workspace.google.com", StringComparison.OrdinalIgnoreCase) ||
               !url.StartsWith("https://photos.google.com", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Bod 10b (Doplněk): Skenuje pohled „Nedávno přidané“ (https://photos.google.com/search/_tra_),
    /// který řadí položky podle data nahrání do Google Photos (nikoliv podle EXIF data pořízení),
    /// takže spolehlivě zachytí i nově nahrané fotky se starým datem pořízení.
    /// </summary>
    public async Task<TimelineScanResult> ScanRecentlyAddedAsync(
        Func<string, bool> isAlreadyKnownMediaKey,
        int maxScrollStepsLimit = 15,
        CancellationToken ct = default)
    {
        await _operationLock.WaitAsync(ct);
        try
        {
            await EnsureInitializedAsync();
            const string recentlyAddedUrl = "https://photos.google.com/search/_tra_";
            Logger.Debug($"Kontroluji pohled 'Nedávno přidané' ({recentlyAddedUrl})...");

            bool navOk = await NavigateAndWaitForDomAsync(recentlyAddedUrl, 15000);
            if (!navOk)
            {
                return new TimelineScanResult
                {
                    Success = false,
                    ReachedTargetBoundary = false,
                    ErrorMessage = "Nepodařilo se načíst pohled 'Nedávno přidané' (_tra_)."
                };
            }

            var currentUrl = _webView.CoreWebView2.Source ?? "";
            if (IsLoginOrMarketingUrl(currentUrl) ||
                !currentUrl.Contains("/search/", StringComparison.OrdinalIgnoreCase))
            {
                return new TimelineScanResult
                {
                    Success = false,
                    ReachedTargetBoundary = false,
                    ErrorMessage = $"Adresa pohledu 'Nedávno přidané' nebyla ověřena (aktuální URL: {currentUrl})."
                };
            }

            await WakeUpVirtualizedGridAsync();

            int domAnchorCount = await WaitForPhotoAnchorsAsync(maxWaitSteps: 10, ct);
            if (domAnchorCount == 0)
            {
                return new TimelineScanResult
                {
                    Success = false,
                    ReachedTargetBoundary = false,
                    ErrorMessage = "Pohled 'Nedávno přidané' (_tra_) neobsahuje žádné odkazy na fotky."
                };
            }

            var itemsById = new Dictionary<string, DiscoveredMediaItem>(StringComparer.Ordinal);
            int consecutiveStepsAllKnown = 0;
            int consecutiveStepsNoNew = 0;
            bool reachedBoundary = false;
            string? lastKey = null;

            for (int step = 0; step < maxScrollStepsLimit; step++)
            {
                ct.ThrowIfCancellationRequested();

                var extracted = await ExtractPhotoAnchorsFromDomAsync();
                int newlyAddedInStep = 0;
                int unseenInStep = 0;

                foreach (var (key, label, dateHint, isVid) in extracted)
                {
                    if (string.IsNullOrEmpty(key) || itemsById.ContainsKey(key))
                        continue;

                    var estimatedDate = ParseDateFromAriaLabel(label) ?? ParseDateFromAriaLabel(dateHint);
                    itemsById[key] = new DiscoveredMediaItem
                    {
                        MediaKey = key,
                        PhotoUrl = $"https://photos.google.com/photo/{key}",
                        AriaLabel = !string.IsNullOrWhiteSpace(label) ? label : dateHint,
                        IsVideo = isVid,
                        EstimatedDate = estimatedDate,
                        DiscoveredFromRecentlyAdded = true
                    };
                    lastKey = key;
                    newlyAddedInStep++;

                    if (!isAlreadyKnownMediaKey(key))
                    {
                        unseenInStep++;
                    }
                }

                if (newlyAddedInStep == 0)
                {
                    consecutiveStepsNoNew++;
                }
                else
                {
                    consecutiveStepsNoNew = 0;
                    if (unseenInStep == 0)
                        consecutiveStepsAllKnown++;
                    else
                        consecutiveStepsAllKnown = 0;
                }

                if (step + 1 >= 2 && consecutiveStepsAllKnown >= 2)
                {
                    reachedBoundary = true;
                    break;
                }

                if (step + 1 >= 3 && consecutiveStepsNoNew >= 4)
                {
                    reachedBoundary = await VerifyTrueEndOfTimelineAsync(ct);
                    break;
                }

                if (step < maxScrollStepsLimit - 1)
                {
                    await ScrollTimelineStepAsync(ct);
                }
            }

            return new TimelineScanResult
            {
                Success = true,
                Items = itemsById.Values.ToList(),
                ReachedTargetBoundary = reachedBoundary,
                LastScannedMediaKey = lastKey
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Skenování pohledu 'Nedávno přidané' selhalo: {ex.Message}");
            return new TimelineScanResult
            {
                Success = false,
                ReachedTargetBoundary = false,
                ErrorMessage = ex.Message
            };
        }
        finally
        {
            _operationLock.Release();
        }
    }

    /// <summary>
    /// P1-7, P1-9 & Bod 10: Skenuje časovou osu Google Photos, rozlišuje selhání skenu od prázdné knihovny,
    /// umí navázat na předchozí nedokončený sken (resumeAfterMediaKey) a konec knihovny ověřuje v DOMu.
    /// </summary>
    public async Task<TimelineScanResult> ScanTimelineAsync(
        Func<string, bool>? isAlreadyKnownMediaKey = null,
        DateTime? cutoffDate = null,
        bool scanAllHistory = false,
        int maxScrollStepsLimit = 60,
        string? resumeAfterMediaKey = null,
        CancellationToken ct = default)
    {
        await _operationLock.WaitAsync(ct);
        try
        {
            await EnsureInitializedAsync();
            var itemsById = new Dictionary<string, DiscoveredMediaItem>(StringComparer.Ordinal);

            Logger.Debug("Otevírám časovou osu Google Photos (https://photos.google.com/)...");
            bool navOk = await NavigateAndWaitForDomAsync("https://photos.google.com/", 18000);
            if (!navOk)
            {
                return new TimelineScanResult
                {
                    Success = false,
                    ReachedTargetBoundary = false,
                    ErrorMessage = "Nepodařilo se načíst časovou osu Google Photos (navigace selhala nebo vypršel čas)."
                };
            }

            var currentUrl = _webView.CoreWebView2.Source ?? "";
            if (IsLoginOrMarketingUrl(currentUrl))
            {
                _isKnownLoggedIn = false;
                _manuallyConfirmedSessionEmail = null;
                return new TimelineScanResult
                {
                    Success = false,
                    ReachedTargetBoundary = false,
                    ErrorMessage = "Přihlášení ke Google Photos vypršelo."
                };
            }

            await WakeUpVirtualizedGridAsync();

            int domAnchorCount = await WaitForPhotoAnchorsAsync(maxWaitSteps: 15, ct);
            currentUrl = _webView.CoreWebView2.Source ?? "";
            Logger.Debug($"Stránka Google Photos načtena ({currentUrl}), počáteční počet DOM odkazů: {domAnchorCount}.");

            if (domAnchorCount == 0)
            {
                // P1-7: Rozlišit prázdnou knihovnu od selhání načtení DOM
                var emptyLibraryCheck = await _webView.CoreWebView2.ExecuteScriptAsync(@"
                    (() => {
                        const txt = document.body ? (document.body.innerText || '') : '';
                        const hasShell = document.querySelector('c-wiz') !== null && document.querySelector('[role=""main""]') !== null;
                        const isExplicitEmpty = /Žádné fotky|Zatím žádné fotky|No photos|Ready to add some photos/i.test(txt);
                        return hasShell && isExplicitEmpty;
                    })();
                ");

                if (emptyLibraryCheck == "true")
                {
                    Logger.Info("Knihovna Google Photos je prázdná (ověřeno).");
                    return new TimelineScanResult
                    {
                        Success = true,
                        Items = [],
                        ReachedTargetBoundary = true
                    };
                }

                return new TimelineScanResult
                {
                    Success = false,
                    ReachedTargetBoundary = false,
                    ErrorMessage = "Časová osa Google Photos se nevykreslila (0 nalezených fotek a nejde o prázdnou knihovnu)."
                };
            }

            bool reachedBoundary = false;
            string? lastScannedKey = null;
            DateTime? oldestScannedDate = null;

            var progress = new TimelineScanProgress(maxScrollStepsLimit, scanAllHistory, hasResumeKey: !string.IsNullOrWhiteSpace(resumeAfterMediaKey));
            var maxResumeSeekDuration = TimeSpan.FromMinutes(5);
            var seekStopwatch = System.Diagnostics.Stopwatch.StartNew();

            while (progress.CanContinue)
            {
                // Posun k místu minulého skenu drží zámek prohlížeče (blokuje mazání i stahování) -> omezit i časem
                if (!progress.PassedResumeKey && seekStopwatch.Elapsed > maxResumeSeekDuration)
                    break;

                ct.ThrowIfCancellationRequested();

                var extracted = await ExtractPhotoAnchorsFromDomAsync();
                int newlyAddedInStep = 0;
                int newUnseenCandidateCountInStep = 0;

                foreach (var (key, label, dateHint, isVid) in extracted)
                {
                    if (string.IsNullOrEmpty(key))
                        continue;

                    if (!progress.PassedResumeKey && string.Equals(key, resumeAfterMediaKey, StringComparison.Ordinal))
                    {
                        progress.MarkResumeKeyReached();
                    }

                    if (itemsById.ContainsKey(key))
                        continue;

                    var estimatedDate = ParseDateFromAriaLabel(label) ?? ParseDateFromAriaLabel(dateHint);
                    if (estimatedDate.HasValue && (!oldestScannedDate.HasValue || estimatedDate.Value < oldestScannedDate.Value))
                    {
                        oldestScannedDate = estimatedDate.Value;
                    }

                    itemsById[key] = new DiscoveredMediaItem
                    {
                        MediaKey = key,
                        PhotoUrl = $"https://photos.google.com/photo/{key}",
                        AriaLabel = !string.IsNullOrWhiteSpace(label) ? label : dateHint,
                        IsVideo = isVid,
                        EstimatedDate = estimatedDate
                    };

                    lastScannedKey = key;
                    newlyAddedInStep++;

                    bool isAlreadyKnown = isAlreadyKnownMediaKey != null && isAlreadyKnownMediaKey(key);
                    bool isOlderThanCutoff = cutoffDate.HasValue &&
                                             estimatedDate.HasValue &&
                                             estimatedDate.Value.Date < cutoffDate.Value.Date;

                    if (!isAlreadyKnown && !isOlderThanCutoff)
                    {
                        newUnseenCandidateCountInStep++;
                    }
                }

                var decision = progress.RecordStep(newlyAddedInStep, newUnseenCandidateCountInStep);
                Logger.Debug($"Skenování časové osy (krok {progress.TotalSteps}{(progress.PassedResumeKey ? "" : ", posun k místu minulého skenu")}): doposud nalezeno {itemsById.Count} položek (+{newlyAddedInStep} v tomto kroku).");

                // P1-9: v běžném režimu (po překonání případného resume bodu) souvislá vrstva již známých/starších položek
                if (decision == TimelineScanDecision.KnownBoundaryReached)
                {
                    reachedBoundary = true;
                    break;
                }

                // Bod 10: nepřibývají položky -> ověřit skutečný konec knihovny v DOMu
                if (decision == TimelineScanDecision.PossibleEnd)
                {
                    if (await VerifyTrueEndOfTimelineAsync(ct))
                    {
                        reachedBoundary = true;
                        break;
                    }

                    if (progress.ShouldGiveUpWithoutConfirmedEnd)
                    {
                        Logger.Warn("Skenování časové osy se zastavilo bez potvrzení konce knihovny (síť nebo DOM stále načítá) – bude pokračovat v dalším cyklu.");
                        reachedBoundary = false;
                        break;
                    }
                }

                if (progress.ShouldScroll)
                {
                    await ScrollTimelineStepAsync(ct);
                }
            }

            if (!progress.PassedResumeKey && !reachedBoundary)
            {
                Logger.Warn("Místo, kde skončil minulý sken časové osy, se nepodařilo v limitu dosáhnout – další cyklus naváže od nejhlubší dosažené položky.");
            }

            if (itemsById.Count == 0)
            {
                return new TimelineScanResult
                {
                    Success = false,
                    ReachedTargetBoundary = false,
                    ErrorMessage = "Nepodařilo se přečíst žádné položky z časové osy Google Photos."
                };
            }

            return new TimelineScanResult
            {
                Success = true,
                Items = itemsById.Values.ToList(),
                ReachedTargetBoundary = reachedBoundary,
                LastScannedMediaKey = lastScannedKey,
                OldestScannedDate = oldestScannedDate,
                ResumeKeyNotReached = !progress.PassedResumeKey && !reachedBoundary
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Error($"Chyba při čtení časové osy Google Photos: {ex.Message}");
            return new TimelineScanResult
            {
                Success = false,
                ReachedTargetBoundary = false,
                ErrorMessage = ex.Message
            };
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private async Task WakeUpVirtualizedGridAsync()
    {
        await _webView.CoreWebView2.ExecuteScriptAsync(@"
            (() => {
                window.dispatchEvent(new Event('resize'));
                window.dispatchEvent(new Event('focus'));
                document.dispatchEvent(new Event('visibilitychange'));
            })();
        ");
    }

    private async Task<int> WaitForPhotoAnchorsAsync(int maxWaitSteps, CancellationToken ct)
    {
        int domAnchorCount = 0;
        for (int wait = 0; wait < maxWaitSteps; wait++)
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

            if (wait == 4 || wait == 8)
            {
                await _webView.CoreWebView2.ExecuteScriptAsync("window.scrollBy(0, 120); window.scrollBy(0, -120);");
            }
        }
        return domAnchorCount;
    }

    private async Task<List<(string Key, string Label, string DateHint, bool IsVid)>> ExtractPhotoAnchorsFromDomAsync()
    {
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

                function queryAllDeep(root) {
                    const anchors = root.querySelectorAll ? Array.from(root.querySelectorAll('a[href*=""photo/""]')) : [];
                    for (const a of anchors) {
                        const href = a.getAttribute('href') || a.href || '';
                        if (href.includes('/album/') && !href.includes('/photo/')) continue;

                        let label = a.getAttribute('aria-label') || '';
                        if (!label) {
                            const img = a.querySelector('img[alt], [aria-label]');
                            if (img) label = img.getAttribute('aria-label') || img.getAttribute('alt') || '';
                        }

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

        var list = new List<(string Key, string Label, string DateHint, bool IsVid)>();
        var rawJson = await _webView.CoreWebView2.ExecuteScriptAsync(extractScript);
        if (!string.IsNullOrWhiteSpace(rawJson) && rawJson != "null")
        {
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
                        list.Add((key, label, dateHint, isVid));
                    }
                }
            }
        }
        return list;
    }

    private async Task ScrollTimelineStepAsync(CancellationToken ct)
    {
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

    /// <summary>
    /// Bod 10: Ověří v DOMu, že jsme skutečně na konci knihovny (žádný aktivní načítací spinner,
    /// posuvník hlavního kontejneru dosáhl spodního okraje a po dalším pokusu o posun se scrollHeight nemění).
    /// </summary>
    private async Task<bool> VerifyTrueEndOfTimelineAsync(CancellationToken ct)
    {
        var currentUrl = _webView.CoreWebView2.Source ?? "";
        if (IsLoginOrMarketingUrl(currentUrl))
            return false;

        const string checkStateJs = @"
            (() => {
                const spinners = Array.from(document.querySelectorAll('[role=""progressbar""], [aria-busy=""true""]'))
                    .filter(el => el.offsetParent !== null && el.getBoundingClientRect().height > 0);
                const hasActiveSpinner = spinners.length > 0;

                let maxScrollHeight = document.scrollingElement ? document.scrollingElement.scrollHeight : 0;
                let reachedBottom = true;

                const all = Array.from(document.querySelectorAll('c-wiz, main, [role=""main""], div'));
                for (const el of all) {
                    if (el.scrollHeight > el.clientHeight + 80 && el.clientHeight > 200) {
                        const style = window.getComputedStyle(el);
                        if (style.overflowY === 'auto' || style.overflowY === 'scroll' || el.tagName === 'C-WIZ') {
                            if (el.scrollHeight > maxScrollHeight) {
                                maxScrollHeight = el.scrollHeight;
                            }
                            if (el.scrollTop + el.clientHeight < el.scrollHeight - 120) {
                                reachedBottom = false;
                            }
                        }
                    }
                }

                const anchors = document.querySelectorAll('a[href*=""photo/""]');
                const lastHref = anchors.length > 0 ? (anchors[anchors.length - 1].getAttribute('href') || '') : '';

                return JSON.stringify({
                    hasActiveSpinner: hasActiveSpinner,
                    reachedBottom: reachedBottom,
                    maxScrollHeight: maxScrollHeight,
                    lastHref: lastHref
                });
            })();
        ";

        var raw1 = await _webView.CoreWebView2.ExecuteScriptAsync(checkStateJs);
        if (string.IsNullOrWhiteSpace(raw1) || raw1 == "null")
            return false;

        var json1 = JsonSerializer.Deserialize<string>(raw1);
        if (string.IsNullOrWhiteSpace(json1))
            return false;

        using var doc1 = JsonDocument.Parse(json1);
        if (doc1.RootElement.GetProperty("hasActiveSpinner").GetBoolean() ||
            !doc1.RootElement.GetProperty("reachedBottom").GetBoolean())
        {
            return false;
        }

        long height1 = doc1.RootElement.GetProperty("maxScrollHeight").GetInt64();
        string lastHref1 = doc1.RootElement.GetProperty("lastHref").GetString() ?? "";

        await ScrollTimelineStepAsync(ct);
        await Task.Delay(500, ct);

        var raw2 = await _webView.CoreWebView2.ExecuteScriptAsync(checkStateJs);
        if (string.IsNullOrWhiteSpace(raw2) || raw2 == "null")
            return false;

        var json2 = JsonSerializer.Deserialize<string>(raw2);
        if (string.IsNullOrWhiteSpace(json2))
            return false;

        using var doc2 = JsonDocument.Parse(json2);
        if (doc2.RootElement.GetProperty("hasActiveSpinner").GetBoolean() ||
            !doc2.RootElement.GetProperty("reachedBottom").GetBoolean())
        {
            return false;
        }

        long height2 = doc2.RootElement.GetProperty("maxScrollHeight").GetInt64();
        string lastHref2 = doc2.RootElement.GetProperty("lastHref").GetString() ?? "";

        return height2 == height1 && string.Equals(lastHref1, lastHref2, StringComparison.Ordinal);
    }

    public async Task<DownloadResult> DownloadMediaAsync(
        DiscoveredMediaItem item,
        string targetFolder,
        bool allowVideos,
        CancellationToken ct = default)
    {
        await _operationLock.WaitAsync(ct);
        CoreWebView2DownloadOperation? activeDownloadOp = null;
        string? activeTempPath = null;
        try
        {
            await EnsureInitializedAsync();

            if (!Directory.Exists(targetFolder))
            {
                return new DownloadResult
                {
                    Success = false,
                    ErrorMessage = $"Cílová složka '{targetFolder}' neexistuje."
                };
            }

            var tcsDownload = new TaskCompletionSource<DownloadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var downloadSync = new object();
            bool downloadStarted = false;

            void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
            {
                string thisTempFilePath;
                string thisOriginalFileName;
                CoreWebView2DownloadOperation op;

                lock (downloadSync)
                {
                    e.Handled = true; // Hide default browser download bar

                    // P1-10: Pokud už jedno stahování pro tuto položku běží, všechna další okamžitě zrušíme
                    if (downloadStarted || tcsDownload.Task.IsCompleted)
                    {
                        e.Cancel = true;
                        return;
                    }

                    downloadStarted = true;
                    op = e.DownloadOperation;
                    activeDownloadOp = op;

                    thisOriginalFileName = Path.GetFileName(e.ResultFilePath);
                    if (string.IsNullOrWhiteSpace(thisOriginalFileName))
                    {
                        var defaultExt = item.IsVideo ? ".mp4" : ".jpg";
                        thisOriginalFileName = $"{item.MediaKey[..Math.Min(12, item.MediaKey.Length)]}{defaultExt}";
                    }

                    var ext = Path.GetExtension(thisOriginalFileName);
                    if (!allowVideos && (item.IsVideo || VideoExtensions.Contains(ext)))
                    {
                        e.Cancel = true;
                        tcsDownload.TrySetResult(new DownloadResult
                        {
                            Success = false,
                            SkippedVideo = true,
                            LocalFileName = thisOriginalFileName
                        });
                        return;
                    }

                    thisTempFilePath = Path.Combine(targetFolder, $".{Guid.NewGuid():N}_{thisOriginalFileName}.part");
                    activeTempPath = thisTempFilePath;
                    e.ResultFilePath = thisTempFilePath;
                }

                op.StateChanged += (_, _) =>
                {
                    try
                    {
                        if (op.State == CoreWebView2DownloadState.Completed)
                        {
                            tcsDownload.TrySetResult(new DownloadResult
                            {
                                Success = true,
                                LocalFileName = thisOriginalFileName,
                                FullPath = thisTempFilePath
                            });
                        }
                        else if (op.State == CoreWebView2DownloadState.Interrupted)
                        {
                            CleanupTempFile(thisTempFilePath);
                            tcsDownload.TrySetResult(new DownloadResult
                            {
                                Success = false,
                                ErrorMessage = $"Stahování přerušeno ({op.InterruptReason})"
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        CleanupTempFile(thisTempFilePath);
                        tcsDownload.TrySetResult(new DownloadResult
                        {
                            Success = false,
                            ErrorMessage = ex.Message
                        });
                    }
                };
            }

            _webView.CoreWebView2.DownloadStarting += OnDownloadStarting;
            try
            {
                Logger.Debug($"Otevírám detail položky pro stažení: {item.PhotoUrl}");
                bool navOk = await NavigateAndWaitForDomAsync(item.PhotoUrl, 15000);
                if (!navOk)
                {
                    return new DownloadResult
                    {
                        Success = false,
                        ErrorMessage = $"Nepodařilo se načíst detail položky '{item.MediaKey}'."
                    };
                }

                await Task.Delay(1500, ct);

                var currentUrl = _webView.CoreWebView2.Source ?? "";
                if (!IsExactPhotoDetailUrl(currentUrl, item.MediaKey))
                {
                    return new DownloadResult
                    {
                        Success = false,
                        ErrorMessage = $"Otevřená adresa '{currentUrl}' neodpovídá požadované položce '{item.MediaKey}'."
                    };
                }

                // Method 1: Trigger Shift+D via CDP + JS KeyboardEvent
                await DispatchShiftDAsync();

                var startCheck1 = await Task.WhenAny(tcsDownload.Task, Task.Delay(3000, ct));
                bool isStarted;
                lock (downloadSync) { isStarted = downloadStarted; }

                if (startCheck1 != tcsDownload.Task && !isStarted)
                {
                    // Method 2: Click 'More options' (three dots) -> 'Download / Stáhnout' in Google Photos UI
                    Logger.Info($"Zkouším stahování přes menu 'Další možnosti -> Stáhnout' pro {item.MediaKey}...");
                    bool menuClicked = await TriggerDownloadFromUiMenuAsync();
                    if (!menuClicked)
                    {
                        Logger.Warn($"Menu 'Další možnosti -> Stáhnout' nebylo u položky '{item.MediaKey}' nalezeno.");
                    }
                }

                var startCheck2 = await Task.WhenAny(tcsDownload.Task, Task.Delay(3500, ct));
                lock (downloadSync) { isStarted = downloadStarted; }

                if (startCheck2 != tcsDownload.Task && !isStarted)
                {
                    // Method 3: Direct googleusercontent.com download link extraction
                    Logger.Info($"Zkouším přímý stahovací odkaz pro {item.MediaKey}...");
                    bool directTriggered = await TriggerDirectGoogleUserContentDownloadAsync(item.IsVideo);
                    if (!directTriggered)
                    {
                        Logger.Warn($"Přímý stahovací odkaz nebyl u položky '{item.MediaKey}' nalezen.");
                    }

                    // P3-22: Počkat krátce na zahájení stahování z 3. metody; pokud vůbec nezačne, nečekat 10 minut
                    await Task.WhenAny(tcsDownload.Task, Task.Delay(4000, ct));
                    lock (downloadSync) { isStarted = downloadStarted; }

                    if (!isStarted && !tcsDownload.Task.IsCompleted)
                    {
                        return new DownloadResult
                        {
                            Success = false,
                            ErrorMessage = "Nepodařilo se spustit stahování položky v rozhraní Google Photos (ovládací prvky pro stažení nebyly nalezeny)."
                        };
                    }
                }

                // Wait for download completion (up to 10 minutes for large videos)
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromMinutes(10));

                Task completedTask;
                try
                {
                    completedTask = await Task.WhenAny(
                        tcsDownload.Task,
                        Task.Delay(Timeout.Infinite, timeoutCts.Token));
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    completedTask = Task.CompletedTask;
                }

                if (completedTask != tcsDownload.Task)
                {
                    try { activeDownloadOp?.Cancel(); } catch { }
                    CleanupTempFile(activeTempPath);
                    ct.ThrowIfCancellationRequested();

                    return new DownloadResult
                    {
                        Success = false,
                        ErrorMessage = "Časový limit stahování vypršel."
                    };
                }

                var res = await tcsDownload.Task;
                if (!res.Success || res.SkippedVideo)
                {
                    CleanupTempFile(res.FullPath);
                    CleanupTempFile(activeTempPath);
                    return res;
                }

                // P1-11 & Bod 11: Ověřit, že stažený soubor odpovídá očekávanému typu a při chybě uklidit .part
                var validation = ExifService.ValidateDownloadedMediaFile(res.FullPath, res.LocalFileName, item.IsVideo);
                if (!validation.IsValid)
                {
                    CleanupTempFile(res.FullPath);
                    CleanupTempFile(activeTempPath);
                    return new DownloadResult
                    {
                        Success = false,
                        ErrorMessage = validation.ErrorMessage
                    };
                }

                // Move .part file to final unique filename in targetFolder
                var finalPath = GetUniqueFilePath(targetFolder, res.LocalFileName);
                File.Move(res.FullPath, finalPath, overwrite: true);

                var takenAt = ExifService.ApplyCaptureTimestampToFile(finalPath, item.EstimatedDate);
                var fileInfo = new FileInfo(finalPath);
                var fileHash = ExifService.ComputeFastFileHash(finalPath);

                return new DownloadResult
                {
                    Success = true,
                    LocalFileName = Path.GetFileName(finalPath),
                    FullPath = finalPath,
                    FileHash = fileHash,
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
            try { activeDownloadOp?.Cancel(); } catch { }
            CleanupTempFile(activeTempPath);
            throw;
        }
        catch (Exception ex)
        {
            try { activeDownloadOp?.Cancel(); } catch { }
            CleanupTempFile(activeTempPath);
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
    /// P0-4, P1-5, P1-6 & Bod 9: Obousměrná synchronizace – přesune fotku/video s daným MediaKey do koše v Google Photos.
    /// Před kliknutím přísně ověří navigaci, shodu URL/MediaKey a aktivní prohlížeč fotky.
    /// Úspěch smazání NIKDY nebere z pouhého opuštění URL: vyžaduje buď explicitní toast „Přesunuto do koše“,
    /// nebo následné ověření nedostupnosti položky při novém načtení její URL.
    /// </summary>
    public async Task<TrashOutcome> MoveToTrashAsync(string mediaKey, string photoUrl, CancellationToken ct = default)
    {
        bool trashClicked = false;
        if (string.IsNullOrWhiteSpace(mediaKey) || mediaKey.Length < 15)
        {
            Logger.Error("Nelze přesunout položku do koše: chybí platný identifikátor MediaKey.");
            return trashClicked ? TrashOutcome.Unconfirmed : TrashOutcome.NotAttempted;
        }

        await _operationLock.WaitAsync(ct);
        try
        {
            await EnsureInitializedAsync();
            var targetUrl = $"https://photos.google.com/photo/{mediaKey}";

            Logger.Debug($"Obousměrná synchronizace: Otevírám položku '{mediaKey}' pro přesun do koše...");

            bool navOk = await NavigateAndWaitForDomAsync(targetUrl, 15000);
            if (!navOk)
            {
                Logger.Error($"Mazání zastaveno: navigace na detail položky '{mediaKey}' selhala nebo vypršel časový limit.");
                return trashClicked ? TrashOutcome.Unconfirmed : TrashOutcome.NotAttempted;
            }

            await Task.Delay(1500, ct);

            var currentUrl = _webView.CoreWebView2.Source ?? "";
            if (IsLoginOrMarketingUrl(currentUrl))
            {
                _isKnownLoggedIn = false;
                _manuallyConfirmedSessionEmail = null;
                Logger.Error($"Mazání zastaveno: uživatel není přihlášen ke Google Photos (URL: {currentUrl}).");
                return trashClicked ? TrashOutcome.Unconfirmed : TrashOutcome.NotAttempted;
            }

            if (!IsExactPhotoDetailUrl(currentUrl, mediaKey))
            {
                Logger.Error($"Mazání zastaveno: otevřená URL '{currentUrl}' neodpovídá položce '{mediaKey}'.");
                return trashClicked ? TrashOutcome.Unconfirmed : TrashOutcome.NotAttempted;
            }

            // P1-6: Přesná kontrola, zda položka již nebyla smazána dříve (bez falešné shody '404' v textu)
            if (await CheckIfPhotoAlreadyDeletedInDomAsync(mediaKey))
            {
                Logger.Info($"Položka '{mediaKey}' již v Google Photos neexistuje (ověřená chybová stránka).");
                return TrashOutcome.Trashed;
            }

            if (await IsPhotoInTrashAsync(mediaKey))
            {
                Logger.Info($"Položka '{mediaKey}' už leží v koši Google Photos – znovu se nemaže.");
                return TrashOutcome.Trashed;
            }

            // P0-4: Ověřit, že je na stránce vykreslen detail správné fotky a kliknout na tlačítko koše v horní liště
            var safeMediaKeyJson = JsonSerializer.Serialize(mediaKey);
            var clickTrashScript = $@"
                (() => {{
                    const expectedKey = {safeMediaKeyJson};
                    const path = window.location.pathname || '';
                    if (!path.endsWith('/photo/' + expectedKey) && !path.includes('/photo/' + expectedKey + '/')) {{
                        return 'ERR_URL_MISMATCH:' + path;
                    }}

                    if (document.querySelector('[role=""dialog""], [role=""alertdialog""]')) {{
                        return 'ERR_DIALOG_ALREADY_OPEN';
                    }}

                    const hasViewer = document.querySelector('img[src*=""googleusercontent.com""], video, [aria-label*=""Zavřít""], [aria-label*=""Close""]') !== null;
                    if (!hasViewer) {{
                        return 'ERR_NO_VIEWER';
                    }}

                    const selectors = [
                        'button[aria-label=""Smazat""]',
                        'button[aria-label=""Přesunout do koše""]',
                        'button[aria-label=""Delete""]',
                        'button[aria-label=""Move to trash""]',
                        'button[aria-label=""Move to bin""]',
                        'button[aria-label*=""Smazat""]',
                        'button[aria-label*=""Přesunout do koše""]',
                        'button[aria-label*=""Delete""]',
                        'button[aria-label*=""Move to trash""]',
                        'button[aria-label*=""Move to bin""]',
                        '[data-delete-origin] button',
                        'div[role=""button""][aria-label=""Smazat""]',
                        'div[role=""button""][aria-label=""Delete""]'
                    ];

                    for (const sel of selectors) {{
                        const btns = Array.from(document.querySelectorAll(sel))
                            .filter(b => b.offsetParent !== null &&
                                         b.getBoundingClientRect().width > 0 &&
                                         b.getBoundingClientRect().top >= 0 &&
                                         b.getBoundingClientRect().top < 160 &&
                                         !b.closest('[role=""dialog""], [role=""alertdialog""]'));
                        if (btns.length === 1) {{
                            btns[0].click();
                            return 'CLICKED';
                        }}
                        if (btns.length > 1) {{
                            btns[btns.length - 1].click();
                            return 'CLICKED';
                        }}
                    }}
                    return 'ERR_NO_TRASH_BUTTON';
                }})();
            ";

            var trashClickRaw = await _webView.CoreWebView2.ExecuteScriptAsync(clickTrashScript);
            var trashClickStatus = JsonSerializer.Deserialize<string>(trashClickRaw ?? "\"\"") ?? "";

            if (trashClickStatus != "CLICKED")
            {
                Logger.Error($"Mazání položky '{mediaKey}' zastaveno – nepodařilo se bezpečně kliknout na ikonu koše ({trashClickStatus}).");
                return trashClicked ? TrashOutcome.Unconfirmed : TrashOutcome.NotAttempted;
            }

            trashClicked = true;
            await Task.Delay(800, ct);

            // 2. P0-4 & P1-5: Najít a potvrdit výhradně modální potvrzovací dialog přesunu do koše (žádný Enter naslepo!)
            bool dialogConfirmed = false;
            for (int attempt = 0; attempt < 6; attempt++)
            {
                var confirmScript = $@"
                    (() => {{
                        const expectedKey = {safeMediaKeyJson};
                        const path = window.location.pathname || '';
                        if (!path.endsWith('/photo/' + expectedKey) && !path.includes('/photo/' + expectedKey + '/')) {{
                            return 'ERR_URL_CHANGED';
                        }}

                        const dialogs = Array.from(document.querySelectorAll('[role=""dialog""], [role=""alertdialog""], .VfPpkd-P5QLlc'))
                            .filter(d => d.offsetParent !== null || d.getBoundingClientRect().width > 0);

                        for (const dlg of dialogs) {{
                            const buttons = Array.from(dlg.querySelectorAll('button, [role=""button""]'))
                                .filter(b => b.offsetParent !== null && b.getBoundingClientRect().width > 0);
                            const confirmBtn = buttons.find(b => {{
                                const t = (b.innerText || b.getAttribute('aria-label') || '').trim();
                                return /^(Přesunout do koše|Smazat|Move to trash|Move to bin|Delete)$/i.test(t);
                            }}) || dlg.querySelector('button[data-mdc-dialog-action=""ok""]');

                            if (confirmBtn) {{
                                confirmBtn.click();
                                return 'CONFIRMED';
                            }}
                        }}
                        return 'WAITING';
                    }})();
                ";

                var confirmRaw = await _webView.CoreWebView2.ExecuteScriptAsync(confirmScript);
                var confirmStatus = JsonSerializer.Deserialize<string>(confirmRaw ?? "\"\"") ?? "";

                if (confirmStatus == "CONFIRMED")
                {
                    dialogConfirmed = true;
                    break;
                }
                if (confirmStatus.StartsWith("ERR_", StringComparison.Ordinal))
                {
                    // Např. Google Photos přesunulo fotku do koše rovnou bez dialogu a přešlo na další -> ověřit výsledek
                    Logger.Warn($"Potvrzovací dialog pro '{mediaKey}' se nezobrazil ({confirmStatus}) – ověřuji výsledek.");
                    break;
                }

                await Task.Delay(450, ct);
            }

            if (!dialogConfirmed)
            {
                Logger.Warn($"Potvrzovací dialog pro '{mediaKey}' nebyl nalezen – ověřuji, zda Google Photos fotku přesto přesunulo do koše.");
            }

            var afterClickSnapshot = await CaptureTrashDiagnosticsAsync();

            // 3. Bod 9: Ověřit skutečný výsledek přesunu do koše.
            // Pouhé opuštění URL NENÍ důkazem smazání! Vyžadujeme buď toast „Přesunuto do koše“,
            // nebo následné ověření nedostupnosti položky při novém načtení její URL.
            for (int verifyAttempt = 0; verifyAttempt < 10; verifyAttempt++)
            {
                await Task.Delay(500, ct);

                var postUrl = _webView.CoreWebView2.Source ?? "";
                if (IsLoginOrMarketingUrl(postUrl))
                {
                    _isKnownLoggedIn = false;
                    _manuallyConfirmedSessionEmail = null;
                    Logger.Error($"Mazání položky '{mediaKey}' selhalo: prohlížeč byl přesměrován na přihlašovací stránku ({postUrl}).");
                    return trashClicked ? TrashOutcome.Unconfirmed : TrashOutcome.NotAttempted;
                }

                var verifyScript = $@"
                    (() => {{
                        const expectedKey = {safeMediaKeyJson};
                        const path = window.location.pathname || '';
                        const urlChangedAway = !path.includes(expectedKey);

                        const bodyText = document.body ? (document.body.innerText || '') : '';
                        const live = Array.from(document.querySelectorAll('[role=""status""], [role=""alert""], [aria-live]'))
                            .map(el => (el.innerText || el.textContent || '').trim()).join(' | ');
                        const toastRe = /Přesunut[oaáy]? do koše|přesunut[oaáy]? do koše|Moved to (the )?(trash|bin)/i;
                        const hasTrashToast = toastRe.test(bodyText) || toastRe.test(live);
                        const hasErrorToast = /Nelze přesunout do koše|Chyba při mazání|Can't move to trash|Couldn't delete/i.test(bodyText);

                        return JSON.stringify({{
                            urlChangedAway: urlChangedAway,
                            hasTrashToast: hasTrashToast,
                            hasErrorToast: hasErrorToast
                        }});
                    }})();
                ";

                var verifyRaw = await _webView.CoreWebView2.ExecuteScriptAsync(verifyScript);
                if (!string.IsNullOrWhiteSpace(verifyRaw) && verifyRaw != "null")
                {
                    var verifyJson = JsonSerializer.Deserialize<string>(verifyRaw);
                    if (!string.IsNullOrWhiteSpace(verifyJson))
                    {
                        using var doc = JsonDocument.Parse(verifyJson);
                        bool urlChangedAway = doc.RootElement.GetProperty("urlChangedAway").GetBoolean();
                        bool hasTrashToast = doc.RootElement.GetProperty("hasTrashToast").GetBoolean();
                        bool hasErrorToast = doc.RootElement.GetProperty("hasErrorToast").GetBoolean();

                        if (hasErrorToast)
                        {
                            Logger.Error($"Google Photos ohlásilo chybu při přesouvání položky '{mediaKey}' do koše.");
                            return trashClicked ? TrashOutcome.Unconfirmed : TrashOutcome.NotAttempted;
                        }

                        if (hasTrashToast)
                        {
                            Logger.Info($"Položka '{mediaKey}' byla ověřeně přesunuta do koše v Google Photos (potvrzeno hláškou 'Přesunuto do koše').");
                            return TrashOutcome.Trashed;
                        }

                        if (urlChangedAway)
                        {
                            // URL se změnila (např. přechod na další fotku), ale toast jsme nezachytili -> přejdeme rovnou na ověření přes opětovné načtení URL
                            break;
                        }
                    }
                }
            }

            // Bod 9: Znovu načíst detail mazané fotky a ověřit, že je skutečně nedostupná (404 / smazaná)
            if (await NavigateAndWaitForDomAsync(targetUrl, 10000))
            {
                await Task.Delay(1000, ct);
                var verifyUrl = _webView.CoreWebView2.Source ?? "";
                if (!IsLoginOrMarketingUrl(verifyUrl) && await IsPhotoInTrashAsync(mediaKey))
                {
                    Logger.Info($"Položka '{mediaKey}' byla ověřeně přesunuta do koše (detail fotky nabízí obnovení z koše).");
                    return TrashOutcome.Trashed;
                }

                if (!IsLoginOrMarketingUrl(verifyUrl) && await CheckIfPhotoAlreadyDeletedInDomAsync(mediaKey))
                {
                    Logger.Info($"Položka '{mediaKey}' byla ověřeně přesunuta do koše (při opětovném načtení již neexistuje).");
                    return TrashOutcome.Trashed;
                }
            }

            Logger.Error($"Nepodařilo se potvrdit, že položka '{mediaKey}' byla skutečně přesunuta do koše. Mazání se z bezpečnostních důvodů NEOPAKUJE – zkontrolujte koš v Google Photos.");
            Logger.Warn($"Diagnostika mazání '{mediaKey}' – po potvrzení: {afterClickSnapshot}");
            Logger.Warn($"Diagnostika mazání '{mediaKey}' – po novém načtení detailu: {await CaptureTrashDiagnosticsAsync()}");
            return trashClicked ? TrashOutcome.Unconfirmed : TrashOutcome.NotAttempted;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Error($"Chyba při přesouvání položky '{mediaKey}' do koše Google Photos: {ex.Message}");
            return trashClicked ? TrashOutcome.Unconfirmed : TrashOutcome.NotAttempted;
        }
        finally
        {
            _operationLock.Release();
        }
    }

    /// <summary>
    /// Diagnostický snímek stránky pro ladění ověření mazání (adresa, titulek, hlášky, popisky tlačítek).
    /// Neobsahuje obsah fotek ani osobní údaje kromě textů ovládacích prvků.
    /// </summary>
    private async Task<string> CaptureTrashDiagnosticsAsync()
    {
        const string script = @"
            (() => {
                const vis = el => el.offsetParent !== null && el.getBoundingClientRect().width > 0;
                const clip = (t, n) => (t || '').replace(/\s+/g, ' ').trim().slice(0, n);
                const live = Array.from(document.querySelectorAll('[role=""status""], [role=""alert""], [aria-live]'))
                    .map(el => clip(el.innerText || el.textContent, 120)).filter(t => t).slice(0, 6);
                const dialogs = Array.from(document.querySelectorAll('[role=""dialog""], [role=""alertdialog""]'))
                    .filter(vis).map(el => clip(el.innerText, 160)).slice(0, 3);
                const buttons = Array.from(document.querySelectorAll('button, [role=""button""]')).filter(vis)
                    .map(b => clip((b.getAttribute('aria-label') || '') + (b.innerText ? ' / ' + b.innerText : ''), 50))
                    .filter(t => t && !t.includes('@')).slice(0, 30);
                const body = document.body ? document.body.innerText || '' : '';
                const kos = [];
                const re = /.{0,40}(koš|trash|bin)\S*.{0,40}/gi;
                let m;
                while ((m = re.exec(body)) !== null && kos.length < 5) kos.push(clip(m[0], 90));
                return JSON.stringify({
                    url: location.href, title: document.title,
                    live: live, dialogs: dialogs, buttons: buttons, kosTexts: kos
                });
            })();
        ";
        try
        {
            var raw = await _webView.CoreWebView2.ExecuteScriptAsync(script);
            return JsonSerializer.Deserialize<string>(raw ?? "\"\"") ?? "";
        }
        catch (Exception ex)
        {
            return $"(snímek se nepodařilo pořídit: {ex.Message})";
        }
    }

    /// <summary>
    /// Text, podle kterého Google Photos označuje položku v koši. Ověřeno na VM: detail fotky v koši
    /// ukazuje ovládací prvek „Zbývá 30 dnů, než bude položka trvale smazána“; anglicky obdobně „… days left …“.
    /// </summary>
    public static bool IsTrashIndicatorText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        return Regex.IsMatch(text,
            @"Zbývá\s+\d+\s+(dn|den)|trvale smazán|(\d+\s+days?\s+(left|remaining|until))|permanently deleted|(^|\s)(Obnovit|Restore)(\s|$)",
            RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// Je otevřený detail fotky v koši? (adresa /trash/ nebo popisek ovládacího prvku „Zbývá N dnů, než bude
    /// položka trvale smazána“, případně tlačítko „Obnovit“).
    /// </summary>
    private async Task<bool> IsPhotoInTrashAsync(string mediaKey)
    {
        const string script = @"
            (() => {
                const path = window.location.pathname || '';
                const labels = Array.from(document.querySelectorAll('button, [role=""button""]'))
                    .filter(b => b.offsetParent !== null && b.getBoundingClientRect().width > 0)
                    .map(b => ((b.getAttribute('aria-label') || '') + ' ' + (b.innerText || '')).replace(/\s+/g, ' ').trim())
                    .filter(t => t);
                return JSON.stringify({ inTrashPath: path.includes('/trash'), labels: labels });
            })();
        ";
        try
        {
            var currentUrl = _webView.CoreWebView2.Source ?? "";
            if (!currentUrl.Contains(mediaKey, StringComparison.Ordinal))
                return false;

            var raw = await _webView.CoreWebView2.ExecuteScriptAsync(script);
            var json = JsonSerializer.Deserialize<string>(raw ?? "\"\"");
            if (string.IsNullOrWhiteSpace(json))
                return false;

            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.GetProperty("inTrashPath").GetBoolean())
                return true;

            foreach (var label in doc.RootElement.GetProperty("labels").EnumerateArray())
            {
                if (IsTrashIndicatorText(label.GetString()))
                    return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsExactPhotoDetailUrl(string? currentUrl, string mediaKey)
    {
        if (string.IsNullOrWhiteSpace(currentUrl) || string.IsNullOrWhiteSpace(mediaKey))
            return false;

        if (!Uri.TryCreate(currentUrl, UriKind.Absolute, out var uri))
            return false;

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.Host, "photos.google.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var path = uri.AbsolutePath.TrimEnd('/');
        return path.EndsWith($"/photo/{mediaKey}", StringComparison.Ordinal);
    }

    /// <summary>
    /// P1-6: Čistá testovatelná logika pro rozlišení skutečné chybové stránky Google Photos od běžné fotky
    /// (nikdy nehledá samotné číslo '404' v celém textu stránky, kde může být v názvu souboru IMG_0404.JPG).
    /// </summary>
    public static bool IsPhotoUnavailablePage(
        string? currentUrl,
        string mediaKey,
        string? pageTitle,
        string? headingText,
        bool hasActivePhotoViewerOrDeleteButton)
    {
        if (hasActivePhotoViewerOrDeleteButton)
            return false;

        if (!IsExactPhotoDetailUrl(currentUrl, mediaKey))
            return false;

        var title = (pageTitle ?? "").Trim();
        var heading = (headingText ?? "").Trim();

        if (Regex.IsMatch(title, @"Error\s+404\b|Chyba\s+404\b|Not\s+Found", RegexOptions.IgnoreCase))
            return true;

        if (Regex.IsMatch(heading, @"Can't access photo|Nelze získat přístup k fotce|Položka nebyla nalezena|404\.\s*That’s an error|404\.\s*That's an error", RegexOptions.IgnoreCase))
            return true;

        return false;
    }

    private async Task<bool> CheckIfPhotoAlreadyDeletedInDomAsync(string mediaKey)
    {
        var checkScript = @"
            (() => {
                const hasViewerOrDelete = document.querySelector(
                    'img[src*=""googleusercontent.com""]:not([src*=""/a/""]), ' +
                    'video, ' +
                    'button[aria-label*=""Smazat""], ' +
                    'button[aria-label*=""Přesunout do koše""], ' +
                    'button[aria-label*=""Delete""], ' +
                    'button[aria-label*=""Move to trash""]'
                ) !== null;

                const headings = Array.from(document.querySelectorAll('h1, h2, [role=""heading""], .error-code, p'))
                    .slice(0, 10)
                    .map(el => (el.innerText || '').trim())
                    .join(' | ');

                return JSON.stringify({
                    url: window.location.href || '',
                    title: document.title || '',
                    headings: headings,
                    hasViewerOrDelete: hasViewerOrDelete
                });
            })();
        ";

        var raw = await _webView.CoreWebView2.ExecuteScriptAsync(checkScript);
        if (string.IsNullOrWhiteSpace(raw) || raw == "null")
            return false;

        var inner = JsonSerializer.Deserialize<string>(raw);
        if (string.IsNullOrWhiteSpace(inner))
            return false;

        using var doc = JsonDocument.Parse(inner);
        var url = doc.RootElement.GetProperty("url").GetString();
        var title = doc.RootElement.GetProperty("title").GetString();
        var headings = doc.RootElement.GetProperty("headings").GetString();
        var hasViewer = doc.RootElement.GetProperty("hasViewerOrDelete").GetBoolean();

        return IsPhotoUnavailablePage(url, mediaKey, title, headings, hasViewer);
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

    private async Task<bool> TriggerDownloadFromUiMenuAsync()
    {
        try
        {
            var openMenuRaw = await _webView.CoreWebView2.ExecuteScriptAsync(@"
                (() => {
                    const moreBtns = Array.from(document.querySelectorAll(
                        'button[aria-label*=""Další možnosti""], button[aria-label*=""More options""], ' +
                        'div[role=""button""][aria-label*=""Další možnosti""], div[role=""button""][aria-label*=""More options""]'
                    )).filter(b => b.offsetParent !== null && b.getBoundingClientRect().width > 0);

                    if (moreBtns.length > 0) {
                        moreBtns[moreBtns.length - 1].click();
                        return true;
                    }
                    return false;
                })();
            ");

            if (openMenuRaw != "true")
                return false;

            await Task.Delay(600);

            var clickDownloadRaw = await _webView.CoreWebView2.ExecuteScriptAsync(@"
                (() => {
                    const items = Array.from(document.querySelectorAll('[role=""menuitem""], [role=""option""], li, button, a'))
                        .filter(el => el.offsetParent !== null && el.getBoundingClientRect().width > 0);
                    const dl = items.find(el => {
                        const txt = (el.innerText || el.getAttribute('aria-label') || '').trim();
                        return /^(Stáhnout|Download)(\b|\s|Shift)/i.test(txt) || /Stáhnout originál|Download original/i.test(txt);
                    });
                    if (dl) {
                        dl.click();
                        return true;
                    }
                    return false;
                })();
            ");

            return clickDownloadRaw == "true";
        }
        catch (Exception ex)
        {
            Logger.Warn($"Chyba při pokusu o stažení přes menu UI: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> TriggerDirectGoogleUserContentDownloadAsync(bool isVideo)
    {
        try
        {
            // P1-11: U videa nejprve hledat skutečný video stream nebo použít parametr '=dv' (nikoliv '=d' na náhledu)
            var suffix = isVideo ? "=dv" : "=d";
            var safeSuffix = JsonSerializer.Serialize(suffix);
            var isVideoJson = isVideo ? "true" : "false";

            var directUrlJson = await _webView.CoreWebView2.ExecuteScriptAsync($@"
                (() => {{
                    const wantVideo = {isVideoJson};
                    if (wantVideo) {{
                        const vid = document.querySelector('video[src*=""googleusercontent.com""], video source[src*=""googleusercontent.com""]');
                        if (vid && vid.src) {{
                            return vid.src;
                        }}
                    }}

                    const imgs = Array.from(document.querySelectorAll('img[src*=""googleusercontent.com""]'))
                        .filter(img => img.src && !img.src.includes('/a/') && !img.src.includes('=s'))
                        .sort((a, b) => (b.naturalWidth * b.naturalHeight || b.clientWidth * b.clientHeight) - (a.naturalWidth * a.naturalHeight || a.clientWidth * a.clientHeight));

                    if (imgs.length > 0) {{
                        let baseUrl = imgs[0].src.split('=')[0];
                        return baseUrl + {safeSuffix};
                    }}
                    return '';
                }})();
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
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Nelze spustit přímé stažení: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// P0-4: Naviguje na zadanou adresu a vrací true pouze tehdy, když navigace skončila v časovém limitu a s IsSuccess == true.
    /// </summary>
    private async Task<bool> NavigateAndWaitForDomAsync(string url, int timeoutMs)
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
            if (completed != tcs.Task)
            {
                Logger.Warn($"Navigace na '{url}' překročila časový limit ({timeoutMs} ms).");
                return false;
            }
            return await tcs.Task;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Navigace na '{url}' selhala: {ex.Message}");
            return false;
        }
        finally
        {
            _webView.CoreWebView2.NavigationCompleted -= Handler;
        }
    }

    /// <summary>
    /// P3-17: Parsuje datum z popisku Google Photos (česky i anglicky).
    /// Pokud v popisku chybí rok a odvozené datum v letošním roce by leželo v budoucnosti, použije předchozí rok.
    /// </summary>
    public static DateTime? ParseDateFromAriaLabel(string? label, DateTime? referenceToday = null)
    {
        if (string.IsNullOrWhiteSpace(label)) return null;

        var today = (referenceToday ?? DateTime.Today).Date;
        var trimmed = label.Trim();

        if (Regex.IsMatch(trimmed, @"\b(dnes|today)\b", RegexOptions.IgnoreCase))
            return today;
        if (Regex.IsMatch(trimmed, @"\b(včera|vcera|yesterday)\b", RegexOptions.IgnoreCase))
            return today.AddDays(-1);

        // 1. Try numeric Czech format: "19. 10. 2026" or "19. 10."
        var czNumMatch = Regex.Match(trimmed, @"(?<d>\d{1,2})\.\s*(?<m>\d{1,2})\.(\s*(?<y>20\d{2}))?");
        if (czNumMatch.Success &&
            int.TryParse(czNumMatch.Groups["m"].Value, out int mNum) && mNum is >= 1 and <= 12 &&
            int.TryParse(czNumMatch.Groups["d"].Value, out int dNum) && dNum is >= 1 and <= 31)
        {
            bool hasExplicitYear = czNumMatch.Groups["y"].Success && int.TryParse(czNumMatch.Groups["y"].Value, out _);
            int yNum = hasExplicitYear ? int.Parse(czNumMatch.Groups["y"].Value) : today.Year;
            var dt = TryConstructDateWithRollover(yNum, mNum, dNum, hasExplicitYear, today);
            if (dt.HasValue) return dt.Value;
        }

        // 2. Try Day + word month format: "19. října 2026", "19. října", "19 Oct 2026", "19 Oct"
        var czWordMatch = Regex.Match(trimmed, @"\b(?<d>\d{1,2})\.?\s+(?<month>[a-zA-ZáčďéěíňóřšťúůýžÁČĎÉĚÍŇÓŘŠŤÚŮÝŽ]+)(?:[,\s]+(?<y>20\d{2}))?\b");
        if (czWordMatch.Success &&
            int.TryParse(czWordMatch.Groups["d"].Value, out int dWord) && dWord is >= 1 and <= 31)
        {
            var mName = czWordMatch.Groups["month"].Value;
            if (CzechMonths.TryGetValue(mName, out int mWord))
            {
                bool hasExplicitYear = czWordMatch.Groups["y"].Success && int.TryParse(czWordMatch.Groups["y"].Value, out _);
                int yWord = hasExplicitYear ? int.Parse(czWordMatch.Groups["y"].Value) : today.Year;
                var dt = TryConstructDateWithRollover(yWord, mWord, dWord, hasExplicitYear, today);
                if (dt.HasValue) return dt.Value;
            }
        }

        // 2b. Try English Month + Day (+ optional Year) format: "Oct 16, 2026" or "Oct 16"
        var enWordMatch = Regex.Match(trimmed, @"\b(?<month>[a-zA-Z]{3,9})\s+(?<d>\d{1,2})(?:[,\s]+(?<y>20\d{2}))?\b");
        if (enWordMatch.Success &&
            int.TryParse(enWordMatch.Groups["d"].Value, out int dEn) && dEn is >= 1 and <= 31)
        {
            var mName = enWordMatch.Groups["month"].Value;
            if (CzechMonths.TryGetValue(mName, out int mEn))
            {
                bool hasExplicitYear = enWordMatch.Groups["y"].Success && int.TryParse(enWordMatch.Groups["y"].Value, out _);
                int yEn = hasExplicitYear ? int.Parse(enWordMatch.Groups["y"].Value) : today.Year;
                var dt = TryConstructDateWithRollover(yEn, mEn, dEn, hasExplicitYear, today);
                if (dt.HasValue) return dt.Value;
            }
        }

        // 3. Try English / International formats inside aria-label: e.g. "Oct 19, 2026", "19 Oct 2026", "Dec 28"
        var parts = trimmed.Split(['-', '–', '—', ',', ':'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        CultureInfo[] cultures = [new("cs-CZ"), CultureInfo.InvariantCulture, new("en-US"), new("en-GB")];

        for (int i = 0; i < parts.Length; i++)
        {
            var candidate = parts[i];
            bool hasYearInCandidate = Regex.IsMatch(candidate, @"\b(19|20)\d{2}\b");
            if (i + 1 < parts.Length && Regex.IsMatch(parts[i + 1], @"^\d{4}"))
            {
                candidate = $"{parts[i]}, {parts[i + 1]}";
                hasYearInCandidate = true;
            }

            foreach (var culture in cultures)
            {
                if (DateTime.TryParse(candidate, culture, DateTimeStyles.AllowWhiteSpaces, out var parsed) &&
                    parsed.Year >= 1995 && parsed.Year <= today.Year + 1)
                {
                    if (!hasYearInCandidate)
                    {
                        var adjusted = TryConstructDateWithRollover(today.Year, parsed.Month, parsed.Day, hasExplicitYear: false, today);
                        if (adjusted.HasValue) return adjusted.Value;
                    }
                    return parsed.Date;
                }
            }
        }

        return null;
    }

    private static DateTime? TryConstructDateWithRollover(int year, int month, int day, bool hasExplicitYear, DateTime referenceToday)
    {
        try
        {
            var dt = new DateTime(year, month, day);
            if (!hasExplicitYear && dt.Date > referenceToday.Date.AddDays(1))
            {
                dt = dt.AddYears(-1);
            }
            return dt;
        }
        catch
        {
            // Např. 29. února v nepřestupném roce -> zkusit předchozí rok, pokud nebyl rok explicitně zadán
            if (!hasExplicitYear)
            {
                try { return new DateTime(year - 1, month, day); } catch { }
            }
            return null;
        }
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
