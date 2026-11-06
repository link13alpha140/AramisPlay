using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using AramisPlay.Services;

namespace AramisPlay;

public class MainWindowForm : Form
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private WebView2? _webView;
    private readonly string _uiUrl;
    private readonly string _webViewProfileDir;

    public MainWindowForm(string uiUrl, string dataDirectory, Icon appIcon)
    {
        _uiUrl = uiUrl;
        _webViewProfileDir = Path.Combine(dataDirectory, "webview2_cache");

        Text = "AramisPlay — Catalogue & Suivi Anime-Sama";
        Icon = appIcon;
        BackColor = Color.FromArgb(11, 14, 23);
        ForeColor = Color.White;
        Size = new Size(1380, 880);
        MinimumSize = new Size(980, 640);
        StartPosition = FormStartPosition.CenterScreen;

        _webView = new WebView2
        {
            Dock = DockStyle.Fill,
            DefaultBackgroundColor = Color.FromArgb(11, 14, 23)
        };
        Controls.Add(_webView);

        Shown += async (_, _) => await InitializeWebViewAsync();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try
        {
            int useDark = 1;
            // DWMWA_USE_IMMERSIVE_DARK_MODE (20 sur Win11 / Win10 20H1+)
            DwmSetWindowAttribute(Handle, 20, ref useDark, sizeof(int));
        }
        catch { }
    }

    private async Task InitializeWebViewAsync()
    {
        if (_webView == null || IsDisposed) return;

        try
        {
            Directory.CreateDirectory(_webViewProfileDir);
            var env = await CoreWebView2Environment.CreateAsync(null, _webViewProfileDir);
            if (_webView == null || IsDisposed) return;

            await _webView.EnsureCoreWebView2Async(env);
            if (_webView?.CoreWebView2 == null || IsDisposed) return;

            _webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            _webView.CoreWebView2.Settings.IsZoomControlEnabled = true;

            // Toute ouverture de nouvel onglet externe (ex: Anime-Sama) part dans le vrai navigateur habituel
            _webView.CoreWebView2.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                if (!string.IsNullOrWhiteSpace(args.Uri))
                {
                    LocalApiServer.OpenBrowser(args.Uri);
                }
            };

            _webView.Source = new Uri(_uiUrl);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Impossible d'initialiser le moteur graphique WebView2 :\n{ex.Message}",
                "AramisPlay",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_webView != null)
            {
                Controls.Remove(_webView);
                _webView.Dispose();
                _webView = null;
            }
        }
        base.Dispose(disposing);
    }
}
