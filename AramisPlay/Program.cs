using System.Windows.Forms;
using AramisPlay.Services;

namespace AramisPlay;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        using var mutex = new Mutex(true, @"Local\AramisPlay_SingleInstance_Mutex", out bool createdNew);
        if (!createdNew)
        {
            // Une instance tourne déjà en veille légère : on lui demande d'afficher sa fenêtre !
            try
            {
                var storage = new StorageService();
                int port = storage.GetSettings().ServerPort;
                if (port <= 1024 || port > 65535) port = 45780;

                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                using var content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
                client.PostAsync($"http://127.0.0.1:{port}/api/system/show-window", content).GetAwaiter().GetResult();
            }
            catch { }
            return;
        }

        ApplicationConfiguration.Initialize();
        bool silent = args.Any(a => a.Equals("--background", StringComparison.OrdinalIgnoreCase) ||
                                    a.Equals("--tray", StringComparison.OrdinalIgnoreCase));

        Application.Run(new TrayApplicationContext(startSilentInTray: silent));
    }
}
