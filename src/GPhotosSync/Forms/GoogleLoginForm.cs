using GPhotosSync.Data;
using GPhotosSync.Services;
using Microsoft.Web.WebView2.WinForms;

namespace GPhotosSync.Forms;

public class GoogleLoginForm : Form
{
    private readonly GooglePhotosEngine _photosEngine;
    private readonly WebView2 _webView;
    private readonly Label _lblStatus;
    private readonly Button _btnDone;
    private readonly System.Windows.Forms.Timer _checkTimer;

    private readonly string? _loginHintEmail;

    public bool LoginSucceeded { get; private set; }

    public GoogleLoginForm(GooglePhotosEngine photosEngine, string? loginHintEmail = null)
    {
        _photosEngine = photosEngine;
        _loginHintEmail = string.IsNullOrWhiteSpace(loginHintEmail) ? null : loginHintEmail.Trim();
        // Přihlašovací okno může přepnout účet -> dřívější ruční potvrzení e-mailu už neplatí
        _photosEngine.ClearConfirmedSessionAccountEmail();

        Text = "Přihlášení k účtu Google Photos";
        Icon = IconGenerator.AppIcon;
        Width = 980;
        Height = 760;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9.5f);

        var topPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 56,
            BackColor = Color.FromArgb(245, 248, 255),
            Padding = new Padding(14, 10, 14, 10)
        };

        _lblStatus = new Label
        {
            Text = "Přihlaste se ke svému účtu Google níže. Jakmile se zobrazí vaše fotky, klikněte na tlačítko 'Hotovo – Jsem přihlášen(a)'.",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(30, 50, 90)
        };

        _btnDone = new Button
        {
            Text = "✓ Hotovo – Jsem přihlášen(a)",
            Dock = DockStyle.Right,
            Width = 220,
            BackColor = Color.FromArgb(26, 115, 232),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand
        };
        _btnDone.FlatAppearance.BorderSize = 0;
        _btnDone.Click += (_, _) =>
        {
            LoginSucceeded = true;
            _photosEngine.IsKnownLoggedIn = true;
            DialogResult = DialogResult.OK;
            Close();
        };

        topPanel.Controls.Add(_lblStatus);
        topPanel.Controls.Add(_btnDone);

        _webView = new WebView2
        {
            Dock = DockStyle.Fill
        };

        Controls.Add(_webView);
        Controls.Add(topPanel);

        _checkTimer = new System.Windows.Forms.Timer { Interval = 2500 };
        _checkTimer.Tick += async (_, _) => await CheckIfPhotosLoadedAsync();

        Load += async (_, _) => await InitializeBrowserAsync();
        FormClosing += (_, _) => _checkTimer.Stop();
    }

    private async Task InitializeBrowserAsync()
    {
        try
        {
            var env = await _photosEngine.GetSharedEnvironmentAsync();
            await _webView.EnsureCoreWebView2Async(env);
            await GooglePhotosEngine.HideWebView2BrandAsync(_webView.CoreWebView2);
            var loginUrl = "https://accounts.google.com/ServiceLogin?service=lh2&continue=https://photos.google.com/";
            if (_loginHintEmail != null)
            {
                // Předvyplnit známý účet, aby při opětovném přihlášení stačilo zadat heslo
                loginUrl += "&Email=" + Uri.EscapeDataString(_loginHintEmail);
            }
            _webView.CoreWebView2.Navigate(loginUrl);
            _checkTimer.Start();
        }
        catch (Exception ex)
        {
            Logger.Error($"Chyba při otevírání přihlašovacího okna: {ex.Message}");
            MessageBox.Show(
                $"Nepodařilo se inicializovat komponentu WebView2:\n{ex.Message}",
                "Chyba WebView2",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private async Task CheckIfPhotosLoadedAsync()
    {
        try
        {
            if (_webView.CoreWebView2 == null) return;
            var url = _webView.CoreWebView2.Source ?? "";
            if (!url.StartsWith("https://photos.google.com", StringComparison.OrdinalIgnoreCase))
                return;

            var hasPhotos = await _webView.CoreWebView2.ExecuteScriptAsync(
                "document.querySelectorAll('a[href*=\"/photo/\"]').length > 0");

            if (hasPhotos == "true")
            {
                LoginSucceeded = true;
                _photosEngine.IsKnownLoggedIn = true;
                _lblStatus.Text = "✓ Přihlášení k Google Photos bylo úspěšně ověřeno! Můžete okno potvrdit tlačítkem vpravo.";
                _lblStatus.ForeColor = Color.FromArgb(20, 130, 50);
                _btnDone.BackColor = Color.FromArgb(24, 145, 65);
            }
        }
        catch
        {
            // Ignore transient script check errors during page navigation
        }
    }
}
