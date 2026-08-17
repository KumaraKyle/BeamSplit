using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using BeamSplit.Core;

namespace BeamSplit.Views;

public partial class SetupPage : UserControl
{
    private readonly AppState _state = AppState.Current;
    private readonly Func<int, Task> _launch;
    private readonly Action _customize;
    private bool _busy;

    public SetupPage(Func<int, Task> launch, Action customize)
    {
        InitializeComponent();
        _launch = launch;
        _customize = customize;

        BtnRecheck.Click += (_, _) => Refresh();
        BtnOpenData.Click += (_, _) => Process.Start("explorer.exe", Paths.AppData);
        BtnFixAll.Click += async (_, _) => await FixAllAsync();
        BtnCustomize.Click += (_, _) => _customize();
        BtnQuickLaunch.Click += async (_, _) => await QuickLaunchAsync();
        CbQuickPlayers.SelectedIndex = Math.Clamp(Math.Max(1, _state.Config.Players.Count) - 1, 0, 3);

        _state.Logged += OnLogged;
        Unloaded += (_, _) => _state.Logged -= OnLogged;

        foreach (var l in _state.Snapshot().TakeLast(80)) Append(l);
        Refresh();
    }

    private async Task QuickLaunchAsync()
    {
        if (_busy) return;
        BtnQuickLaunch.IsEnabled = false;
        LblQuickHint.Text = "Checking setup...";
        try
        {
            await FixAllAsync();
            var blockers = SetupStatus.Blockers(_state.Config);
            if (blockers.Count > 0)
            {
                LblQuickHint.Text = "One item still needs you: " + blockers[0];
                return;
            }
            var players = CbQuickPlayers.SelectedIndex + 1;
            LblQuickHint.Text = $"Launching {players} players in parallel...";
            await _launch(players);
            LblQuickHint.Text = "Running. Input isolation is active for the assigned controllers.";
        }
        finally { BtnQuickLaunch.IsEnabled = true; }
    }

    private void OnLogged(LogLine line) => Dispatcher.Invoke(() => Append(line));

    private void Append(LogLine line)
    {
        LogText.Text += (LogText.Text.Length > 0 ? "\n" : "") + line;
        LogScroll.ScrollToEnd();
    }

    public void Refresh()
    {
        var items = SetupStatus.Evaluate(_state.Config);
        Items.Items.Clear();
        foreach (var item in items) Items.Items.Add(BuildRow(item));

        var ready = items.Count(i => i.Ok);
        var blockers = items.Count(i => i.Essential && !i.Ok);
        LblSummary.Text = blockers == 0
            ? $"{ready}/{items.Count} ready  -  good to launch"
            : $"{ready}/{items.Count} ready  -  {blockers} blocking";
        LblSummary.Foreground = (Brush)FindResource(blockers == 0 ? "Good" : "Warn");
    }

    private UIElement BuildRow(SetupItem item)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var dot = new Ellipse
        {
            Width = 9,
            Height = 9,
            Margin = new Thickness(0, 6, 12, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Fill = (Brush)FindResource(item.Ok ? "Good" : item.Essential ? "Bad" : "Warn")
        };
        // gentle pulse on anything still outstanding, so the eye goes there
        if (!item.Ok)
        {
            var pulse = new DoubleAnimation(1, 0.35, TimeSpan.FromMilliseconds(900))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            dot.BeginAnimation(OpacityProperty, pulse);
        }
        Grid.SetColumn(dot, 0);
        grid.Children.Add(dot);

        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = item.Name, FontSize = 13.5 });
        text.Children.Add(new TextBlock
        {
            Text = item.Detail,
            FontSize = 11.5,
            Foreground = (Brush)FindResource("Muted"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 12, 0)
        });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        if (!item.Ok && !string.IsNullOrEmpty(item.Action))
        {
            var btn = new Button
            {
                Content = item.Action,
                Style = (Style)FindResource("Small"),
                VerticalAlignment = VerticalAlignment.Top,
                Tag = item.Key
            };
            btn.Click += async (_, _) => await FixAsync(item.Key);
            Grid.SetColumn(btn, 2);
            grid.Children.Add(btn);
        }

        return grid;
    }

    private async Task FixAllAsync()
    {
        if (_busy) return;
        foreach (var key in new[] { "game", "launcher", "proxy", "protoinput", "devreorder", "server", "mod" })
        {
            var item = SetupStatus.Evaluate(_state.Config).FirstOrDefault(i => i.Key == key);
            if (item is { Ok: false }) await FixAsync(key, refresh: false);
        }
        Refresh();
        _state.Log("Ran every automatic fix.");
    }

    private async Task FixAsync(string key, bool refresh = true)
    {
        if (_busy) return;
        _busy = true;
        BtnFixAll.IsEnabled = false;
        try
        {
            var cfg = _state.Config;
            switch (key)
            {
                case "game":
                {
                    var all = Detect.FindAllBeamNG();
                    if (all.Count > 0)
                    {
                        cfg.GameRoot = all[0];
                        _state.Log($"BeamNG: {all[0]}");
                        if (all.Count > 1)
                            _state.Log($"Note: {all.Count} installs found. Pick the right one on Settings if this is wrong.");
                        foreach (var p in all.Skip(1)) _state.Log($"  also: {p}");
                    }
                    else _state.Log("BeamNG not found - set it manually on Settings.");
                    break;
                }
                case "launcher":
                {
                    var p = Detect.FindLauncher();
                    if (p != null) { cfg.LauncherExe = p; _state.Log($"BeamMP launcher: {p}"); }
                    else _state.Log("BeamMP launcher not found - install BeamMP first.");
                    break;
                }
                case "mod":
                {
                    var major = Detect.GameMajor(Detect.GameVersion(cfg));
                    var match = await BeamMpCatalog.FindMatchingAsync(major, _state.Progress());
                    if (match.ZipPath != null) cfg.ModZip = match.ZipPath;
                    break;
                }
                case "server":
                {
                    var dir = await BeamMpCatalog.DownloadServerAsync(_state.Progress());
                    if (dir != null)
                    {
                        cfg.ServerDir = dir;
                        await ServerConfig.InitializeConfigAsync(cfg, _state.Progress());
                        _state.Log("Server installed. It needs an AuthKey before it will start.");
                    }
                    break;
                }
                case "authkey":
                    Process.Start(new ProcessStartInfo("https://keymaster.beammp.com") { UseShellExecute = true });
                    _state.Log("Opened keymaster - paste the key on the Server page.");
                    break;

                case "proxy":
                case "protoinput":
                    NativeAssets.Extract(_state.Progress());
                    break;

                case "devreorder":
                    if (!NativeAssets.LocateDevreorder(_state.Progress())
                        && !await NativeAssets.DownloadDevreorderAsync(_state.Progress()))
                    {
                        Process.Start(new ProcessStartInfo("https://github.com/briankendall/devreorder") { UseShellExecute = true });
                        // System.Windows.Shapes.Path is in scope here, so be explicit
                        _state.Log($"Could not install devreorder automatically. Put the x64 dinput8.dll in {System.IO.Path.Combine(Paths.BinDir, "dinput8.dll")}");
                    }
                    break;

                case "instances":
                    _state.Log("Instances are built on the first launch - use the Play button.");
                    break;
            }
            _state.Save();
        }
        catch (Exception ex)
        {
            _state.Log($"Fix '{key}' failed: {ex.Message}");
        }
        finally
        {
            _busy = false;
            BtnFixAll.IsEnabled = true;
            if (refresh) Refresh();
        }
    }
}
