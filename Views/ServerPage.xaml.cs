using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using BeamSplit.Core;

namespace BeamSplit.Views;

public partial class ServerPage : UserControl
{
    private readonly AppState _state = AppState.Current;
    private readonly DispatcherTimer _timer;

    public ServerPage()
    {
        InitializeComponent();

        BtnSave.Click += (_, _) => Save();
        BtnStart.Click += (_, _) => { ServerConfig.Start(_state.Config); _state.Log("Server starting..."); };
        BtnStop.Click += (_, _) => { ServerConfig.Stop(); _state.Log("Server stopped."); };
        BtnKeymaster.Click += (_, _) => Open("https://keymaster.beammp.com");
        BtnOpen.Click += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(_state.Config.ServerDir) && Directory.Exists(_state.Config.ServerDir))
                Process.Start("explorer.exe", _state.Config.ServerDir);
        };

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => RefreshLive();
        Loaded += (_, _) => { Load(); _timer.Start(); };
        Unloaded += (_, _) => _timer.Stop();
    }

    private static void Open(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    private void Load()
    {
        var cfg = _state.Config;

        // The page is useless without a server installed - say what to do rather than
        // showing a form that writes nowhere.
        if (string.IsNullOrWhiteSpace(cfg.ServerDir) || !File.Exists(Path.Combine(cfg.ServerDir, "BeamMP-Server.exe")))
        {
            ShowWarning("No BeamMP server installed yet. BeamSplit can download the official server for you.",
                "Download server", async () =>
                {
                    var dir = await BeamMpCatalog.DownloadServerAsync(_state.Progress());
                    if (dir != null)
                    {
                        cfg.ServerDir = dir;
                        await ServerConfig.InitializeConfigAsync(cfg, _state.Progress());
                        _state.Save();
                        Load();
                    }
                });
            SetFormEnabled(false);
            return;
        }

        var toml = ServerConfig.TomlPath(cfg)!;
        if (!File.Exists(toml))
        {
            ShowWarning("The server hasn't generated its config yet. Running it once creates ServerConfig.toml.",
                "Generate config", async () =>
                {
                    await ServerConfig.InitializeConfigAsync(cfg, _state.Progress());
                    Load();
                });
            SetFormEnabled(false);
            return;
        }

        SetFormEnabled(true);
        var s = ServerConfig.Read(cfg);
        TxtName.Text = s.GetValueOrDefault("Name", "BeamSplit");
        TxtPort.Text = s.GetValueOrDefault("Port", "30814");
        TxtMaxPlayers.Text = s.GetValueOrDefault("MaxPlayers", "2");
        TxtMaxCars.Text = s.GetValueOrDefault("MaxCars", "2");
        TxtDesc.Text = s.GetValueOrDefault("Description", "");
        TxtAuth.Text = s.GetValueOrDefault("AuthKey", "");
        ChkPrivate.IsChecked = s.GetValueOrDefault("Private", "true") == "true";
        ChkGuests.IsChecked = s.GetValueOrDefault("AllowGuests", "true") == "true";
        ChkDebug.IsChecked = s.GetValueOrDefault("Debug", "false") == "true";

        var map = s.GetValueOrDefault("Map", "");
        var found = false;
        foreach (ComboBoxItem item in CbMap.Items)
        {
            if ((string)item.Content != map) continue;
            CbMap.SelectedItem = item;
            found = true;
            break;
        }
        if (!found && map.Length > 0)
        {
            CbMap.Items.Add(new ComboBoxItem { Content = map });
            CbMap.SelectedIndex = CbMap.Items.Count - 1;
        }

        if (!ServerConfig.HasAuthKey(cfg))
        {
            ShowWarning("No AuthKey set. The server exits immediately without one - it is required even when Private is on.",
                "Get an AuthKey", () => { Open("https://keymaster.beammp.com"); return Task.CompletedTask; });
        }
        else HideWarning();

        RefreshLive();
    }

    private void ShowWarning(string text, string action, Func<Task> onClick)
    {
        LblWarn.Text = text;
        BtnWarnAction.Content = action;
        BtnWarnAction.Visibility = Visibility.Visible;
        WarnBox.Visibility = Visibility.Visible;
        BtnWarnAction.Click -= AnyHandler;
        _warnAction = onClick;
        BtnWarnAction.Click += AnyHandler;
    }

    private Func<Task>? _warnAction;
    private async void AnyHandler(object s, RoutedEventArgs e)
    {
        if (_warnAction != null) await _warnAction();
    }

    private void HideWarning() => WarnBox.Visibility = Visibility.Collapsed;

    private void SetFormEnabled(bool on)
    {
        foreach (var c in new Control[] { TxtName, TxtPort, TxtMaxPlayers, TxtMaxCars, TxtDesc, TxtAuth, CbMap, ChkPrivate, ChkGuests, ChkDebug, BtnSave, BtnStart, BtnStop })
            c.IsEnabled = on;
    }

    private void Save()
    {
        var vals = new Dictionary<string, string>
        {
            ["Name"] = TxtName.Text,
            ["Port"] = TxtPort.Text,
            ["MaxPlayers"] = TxtMaxPlayers.Text,
            ["MaxCars"] = TxtMaxCars.Text,
            ["Description"] = TxtDesc.Text,
            ["AuthKey"] = TxtAuth.Text,
            ["Map"] = (CbMap.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "",
            ["Private"] = ChkPrivate.IsChecked == true ? "true" : "false",
            ["AllowGuests"] = ChkGuests.IsChecked == true ? "true" : "false",
            ["Debug"] = ChkDebug.IsChecked == true ? "true" : "false"
        };

        if (ServerConfig.Write(_state.Config, vals))
        {
            _state.Log("Server config saved." + (ServerConfig.IsRunning() ? " Restart the server to apply." : ""));
            if (ServerConfig.HasAuthKey(_state.Config)) HideWarning();
        }
        else _state.Log("Could not write ServerConfig.toml.");
    }

    private void RefreshLive()
    {
        var running = ServerConfig.IsRunning();
        Dot.Fill = (Brush)FindResource(running ? "Good" : "Faint");
        LblState.Text = running ? "online" : "offline";
        BtnStart.IsEnabled = !running && ServerConfig.HasAuthKey(_state.Config);
        BtnStop.IsEnabled = running;

        // players, parsed from the server log
        var players = new List<string>();
        var log = ServerConfig.LogPath(_state.Config);
        if (running && log != null && File.Exists(log))
        {
            try
            {
                using var fs = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var sr = new StreamReader(fs);
                var lines = new Queue<string>(200);
                string? l;
                while ((l = sr.ReadLine()) != null)
                {
                    if (lines.Count == 200) lines.Dequeue();
                    lines.Enqueue(LogTail.Clean(l));
                }
                foreach (var line in lines)
                {
                    var join = System.Text.RegularExpressions.Regex.Match(line, @"Assigned ID \d+ to (\S+)");
                    if (join.Success && !players.Contains(join.Groups[1].Value)) players.Add(join.Groups[1].Value);
                    var left = System.Text.RegularExpressions.Regex.Match(line, @"(\S+) Connection Terminated");
                    if (left.Success) players.Remove(left.Groups[1].Value);
                }
            }
            catch { }
        }

        PlayerList.Items.Clear();
        foreach (var p in players)
            PlayerList.Items.Add(new TextBlock { Text = "  " + p, FontSize = 12, Foreground = (Brush)FindResource("Good") });
        LblNoPlayers.Visibility = players.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
