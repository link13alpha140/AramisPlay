using System.Drawing;
using System.Windows.Forms;
using AramisPlay.Models;
using AramisPlay.Services;

namespace AramisPlay;

public class TrayApplicationContext : ApplicationContext
{
    private readonly SynchronizationContext _uiContext;
    private readonly StorageService _storage;
    private readonly AniListService _aniList;
    private readonly AnimeSamaService _animeSama;
    private readonly EpisodeCheckerService _checker;
    private readonly DiscoveryService _discovery;
    private readonly LocalApiServer _server;

    private readonly Icon _normalIcon;
    private readonly Icon _unreadIcon;
    private readonly NotifyIcon _trayIcon;
    private readonly System.Windows.Forms.Timer _periodicTimer;

    private MainWindowForm? _mainWindow;
    private bool _isExitingCompletely;
    private bool _shownTrayHintOnce;
    private string? _lastBalloonWatchUrl;

    public TrayApplicationContext(bool startSilentInTray = false)
    {
        _uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        _storage = new StorageService();
        _aniList = new AniListService(_storage);
        _animeSama = new AnimeSamaService(_storage);
        _checker = new EpisodeCheckerService(_storage, _animeSama);
        _discovery = new DiscoveryService(_storage, _aniList, _animeSama);

        _normalIcon = IconGenerator.CreateAppIcon(64, hasNotificationDot: false);
        _unreadIcon = IconGenerator.CreateAppIcon(64, hasNotificationDot: true);

        // S'assure que les icônes PNG de l'extension navigateur existent
        var rootDir = Directory.GetParent(_storage.DataDirectory)?.FullName;
        if (rootDir != null)
        {
            IconGenerator.EnsureExtensionIcons(Path.Combine(rootDir, "extension", "icons"));
        }

        _server = new LocalApiServer(
            _storage,
            _aniList,
            _animeSama,
            _checker,
            _discovery,
            requestQuitApp: () => _uiContext.Post(_ => ExitCompletely(), null),
            requestShowWindow: () => _uiContext.Post(_ => ShowMainWindow(), null));

        // Menu clic droit de la barre système Windows
        var trayMenu = new ContextMenuStrip();
        var openItem = new ToolStripMenuItem("Ouvrir AramisPlay", null, (_, _) => ShowMainWindow())
        {
            Font = new Font(SystemFonts.MenuFont ?? Control.DefaultFont, FontStyle.Bold)
        };
        var checkNowItem = new ToolStripMenuItem("Vérifier les sorties VOSTFR", null, async (_, _) =>
        {
            var found = await _checker.CheckAllFollowedAsync();
            if (found.Count == 0)
            {
                _trayIcon?.ShowBalloonTip(
                    3000,
                    "AramisPlay — À jour",
                    "Aucun nouvel épisode VOSTFR sorti depuis la dernière vérification.",
                    ToolTipIcon.Info);
            }
        });
        var openAsItem = new ToolStripMenuItem("Ouvrir Anime-Sama", null, (_, _) =>
        {
            LocalApiServer.OpenBrowser(_animeSama.BaseUrl);
        });
        var quitItem = new ToolStripMenuItem("Quitter complètement (0 Mo RAM)", null, (_, _) => ExitCompletely());

        trayMenu.Items.Add(openItem);
        trayMenu.Items.Add(checkNowItem);
        trayMenu.Items.Add(openAsItem);
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add(quitItem);

        _trayIcon = new NotifyIcon
        {
            Icon = HasUnreadNotifications() ? _unreadIcon : _normalIcon,
            Text = "AramisPlay — Suivi & Notifications Anime-Sama",
            ContextMenuStrip = trayMenu,
            Visible = true
        };

        _trayIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                ShowMainWindow();
        };

        _trayIcon.BalloonTipClicked += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(_lastBalloonWatchUrl))
            {
                var target = _lastBalloonWatchUrl;
                _lastBalloonWatchUrl = null;
                LocalApiServer.OpenBrowser(target);
            }
            else
            {
                ShowMainWindow();
            }
        };

        _checker.OnNewNotifications += notifications =>
        {
            _uiContext.Post(_ => HandleNewNotifications(notifications), null);
        };

        _periodicTimer = new System.Windows.Forms.Timer { Interval = 60_000 };
        _periodicTimer.Tick += async (_, _) => await OnTimerTickAsync();
        _periodicTimer.Start();

        _ = InitializeStartupAsync(startSilentInTray);
    }

    private async Task InitializeStartupAsync(bool startSilentInTray)
    {
        await _server.StartAsync();

        if (!startSilentInTray)
        {
            _uiContext.Post(_ => ShowMainWindow(), null);
        }

        // Vérification automatique des sorties VOSTFR au lancement (après affichage rapide de la fenêtre)
        await Task.Delay(1800);
        await _checker.CheckAllFollowedAsync();
    }

    private async Task OnTimerTickAsync()
    {
        if (_checker.IsChecking) return;

        int intervalMin = Math.Max(5, _storage.GetSettings().CheckIntervalMinutes);
        if (_checker.LastRunAt == null ||
            DateTimeOffset.UtcNow - _checker.LastRunAt.Value >= TimeSpan.FromMinutes(intervalMin))
        {
            await _checker.CheckAllFollowedAsync();
        }
    }

    private void HandleNewNotifications(List<AppNotification> notifications)
    {
        if (notifications.Count == 0) return;

        _trayIcon.Icon = _unreadIcon;

        if (!_storage.GetSettings().EnableWindowsNotifications)
            return;

        if (notifications.Count == 1)
        {
            var n = notifications[0];
            _lastBalloonWatchUrl = n.WatchUrl;
            _trayIcon.ShowBalloonTip(
                6000,
                $"Nouvel épisode — {n.AnimeTitle}",
                $"{n.Message}\nClique pour lancer l'épisode sur Anime-Sama.",
                ToolTipIcon.Info);
        }
        else
        {
            _lastBalloonWatchUrl = null;
            var titles = string.Join(", ", notifications.Take(3).Select(x => $"{x.AnimeTitle} (Ép. {x.NewEpisode})"));
            if (notifications.Count > 3) titles += "…";
            _trayIcon.ShowBalloonTip(
                6500,
                $"{notifications.Count} nouveaux épisodes VOSTFR disponibles",
                $"{titles}\nClique pour ouvrir AramisPlay.",
                ToolTipIcon.Info);
        }
    }

    private bool HasUnreadNotifications() =>
        _storage.GetNotifications().Any(n => !n.IsRead);

    public void ShowMainWindow()
    {
        _trayIcon.Icon = HasUnreadNotifications() ? _unreadIcon : _normalIcon;

        if (_mainWindow != null && !_mainWindow.IsDisposed)
        {
            if (_mainWindow.WindowState == FormWindowState.Minimized)
                _mainWindow.WindowState = FormWindowState.Normal;
            _mainWindow.Activate();
            _mainWindow.BringToFront();
            return;
        }

        _mainWindow = new MainWindowForm(_server.BaseAddress, _storage.DataDirectory, _normalIcon);
        _mainWindow.FormClosing += OnMainWindowClosing;
        _mainWindow.FormClosed += OnMainWindowClosed;
        _mainWindow.Show();
        _mainWindow.Activate();
    }

    private void OnMainWindowClosing(object? sender, FormClosingEventArgs e)
    {
        if (_isExitingCompletely) return;

        // Si l'utilisateur a choisi "quit" -> fermeture à 100% (0 Mo RAM)
        var closeMode = _storage.GetSettings().CloseBehavior;
        if (string.Equals(closeMode, "quit", StringComparison.OrdinalIgnoreCase))
        {
            _isExitingCompletely = true;
        }
    }

    private void OnMainWindowClosed(object? sender, FormClosedEventArgs e)
    {
        _mainWindow = null;

        if (_isExitingCompletely)
        {
            ExitCompletely();
            return;
        }

        // Mode Veille légère : la fenêtre et WebView2 ont été intégralement détruits, libérant GPU + RAM !
        GC.Collect();
        GC.WaitForPendingFinalizers();

        if (!_shownTrayHintOnce)
        {
            _shownTrayHintOnce = true;
            _lastBalloonWatchUrl = null;
            _trayIcon.ShowBalloonTip(
                3500,
                "AramisPlay — Veille légère activée",
                "Interface déchargée de la RAM ! Je surveille tes sorties VOSTFR et l'extension en arrière-plan.",
                ToolTipIcon.None);
        }
    }

    public void ExitCompletely()
    {
        _isExitingCompletely = true;
        _periodicTimer.Stop();
        _periodicTimer.Dispose();

        if (_mainWindow != null && !_mainWindow.IsDisposed)
        {
            _mainWindow.FormClosing -= OnMainWindowClosing;
            _mainWindow.FormClosed -= OnMainWindowClosed;
            _mainWindow.Close();
            _mainWindow.Dispose();
            _mainWindow = null;
        }

        _trayIcon.Visible = false;
        _trayIcon.Dispose();

        _ = Task.Run(async () =>
        {
            await _server.StopAsync();
        }).Wait(TimeSpan.FromSeconds(2));

        _normalIcon.Dispose();
        _unreadIcon.Dispose();

        ExitThread();
        Environment.Exit(0);
    }
}
