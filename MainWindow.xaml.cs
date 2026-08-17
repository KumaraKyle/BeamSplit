using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using BeamSplit.Core;
using BeamSplit.Views;

namespace BeamSplit;

public partial class MainWindow : Window
{
    private readonly AppState _state = AppState.Current;
    private readonly Launcher _launcher;
    private readonly FocusGuard _focus;
    private readonly LogHub _logs;
    private readonly SessionMonitor _session;
    private readonly ConsolePanel _console;

    private readonly Dictionary<RadioButton, Func<UIElement>> _pages = new();
    private RadioButton? _current;

    public MainWindow()
    {
        InitializeComponent();

        _launcher = new Launcher(_state);
        _focus = new FocusGuard(_state);
        _logs = new LogHub(_state);
        _session = new SessionMonitor(_state);
        _session.Start();
        _console = new ConsolePanel { CommandHandler = RunCommandAsync };
        ConsoleHost.Child = _console;

        // Open on the PRIMARY monitor. Centring across a multi-monitor desktop can put
        // the window on a screen that is off to one side - the PowerShell version did
        // that and the window effectively "disappeared".
        var pm = Native.GetPrimaryMonitor();
        Left = pm.X + Math.Max(0, (pm.Width - Width) / 2);
        Top = pm.Y + Math.Max(0, (pm.Height - Height) / 2);

        _pages[NavSetup] = () => new SetupPage(LaunchAsync, () => NavScreens.IsChecked = true);
        _pages[NavScreens] = () => new ScreensPage(LaunchAsync, RetileRunningAsync);
        _pages[NavServer] = () => new ServerPage();
        _pages[NavSession] = () => new SessionPage(_session,
            LaunchAsync,
            RetileRunningAsync,
            () => { _launcher.StopAll(_state.Progress()); _focus.Stop(); });
        _pages[NavSettings] = () => new SettingsPage(RebuildAsync);

        foreach (var nav in _pages.Keys)
            nav.Checked += (s, _) => Navigate((RadioButton)s!);

        BtnConsole.Click += (_, _) => ToggleConsole();
        InputBindings.Add(new KeyBinding(new RelayCommand(ToggleConsole),
            new KeyGesture(Key.OemTilde, ModifierKeys.Control)));

        _focus.Changed += () => Dispatcher.BeginInvoke(UpdateStatusDots);

        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) => UpdateStatusDots();
        timer.Start();

        _logs.Rebuild(_state.Config);

        Loaded += (_, _) => Navigate(NavSetup);
        Closed += (_, _) => { _focus.Stop(); _session.Stop(); _logs.Dispose(); _state.Save(); };
    }

    public void SetStatus(string text) => LblStatus.Text = text;

    // ------------------------------------------------------------------ status
    private void UpdateStatusDots()
    {
        var serverUp = ServerConfig.IsRunning();
        DotServer.Fill = (Brush)FindResource(serverUp ? "Good" : "Faint");
        LblServer.Text = serverUp ? "server online" : "server offline";

        var games = Tiling.GameWindows().Count;
        DotGames.Fill = (Brush)FindResource(games > 0 ? "Good" : "Faint");
        LblGames.Text = games == 1 ? "1 instance" : $"{games} instances";

        // A focused game window silently merges every pad into that instance, so this
        // indicator is deliberately prominent.
        if (_state.Config.UseProtoInput && NativeAssets.ProtoInputReady)
        {
            DotFocus.Fill = (Brush)FindResource("Good");
            LblFocus.Text = "Proto Input isolation";
        }
        else if (!_focus.Running)
        {
            DotFocus.Fill = (Brush)FindResource("Faint");
            LblFocus.Text = "focus guard off";
        }
        else if (_focus.GameHasFocus)
        {
            DotFocus.Fill = (Brush)FindResource("Warn");
            LblFocus.Text = "game focused - pads merged";
        }
        else
        {
            DotFocus.Fill = (Brush)FindResource("Good");
            LblFocus.Text = _focus.Reparks > 0 ? $"focus parked ({_focus.Reparks})" : "focus parked";
        }
    }

    // -------------------------------------------------------------- navigation
    private void Navigate(RadioButton nav)
    {
        if (_current == nav) return;
        _current = nav;
        PageHost.Content = _pages[nav]();

        PageHost.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        ((TranslateTransform)PageHost.RenderTransform).BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(200))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    private bool _consoleOpen;

    private void ToggleConsole()
    {
        _consoleOpen = !_consoleOpen;
        ConsoleHost.Visibility = Visibility.Visible;
        var to = _consoleOpen ? 260d : 0d;
        var anim = new DoubleAnimation(ConsoleHost.ActualHeight, to, TimeSpan.FromMilliseconds(180))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        anim.Completed += (_, _) => { if (!_consoleOpen) ConsoleHost.Visibility = Visibility.Collapsed; };
        ConsoleHost.BeginAnimation(HeightProperty, anim);
    }

    // ---------------------------------------------------------------- commands
    /// <summary>
    /// The console command bar. Deliberately NOT a shell - it drives the app's own
    /// actions, so there is no PowerShell dependency and no surprise about scope.
    /// </summary>
    private async Task RunCommandAsync(string cmd)
    {
        var parts = cmd.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return;
        var verb = parts[0].ToLowerInvariant();
        var cfg = _state.Config;

        try
        {
            switch (verb)
            {
                case "launch":
                {
                    var n = parts.Length > 1 && int.TryParse(parts[1], out var v) ? v : Math.Max(2, cfg.Players.Count);
                    await LaunchAsync(n);
                    break;
                }
                case "stop":
                    _launcher.StopAll(_state.Progress());
                    _focus.Stop();
                    break;

                case "retile":
                    await _launcher.TileAsync(_state.Progress());
                    break;

                case "park":
                    _state.Log(Tiling.ParkFocus() ? "Focus parked." : "Could not find the shell window.");
                    break;

                case "assign":
                    if (parts.Length >= 3 && int.TryParse(parts[1], out var inst) && int.TryParse(parts[2], out var pad))
                    {
                        var live = InputSetup.SetPad(cfg, inst, pad);
                        if (inst < cfg.Players.Count) cfg.Players[inst].Pad = pad;
                        _state.Save();
                        _state.Log(live
                            ? $"P{inst} -> pad {pad} (applied immediately)"
                            : $"P{inst} -> pad {pad} (saved for next launch)");
                    }
                    else _state.Log("usage: assign <instance> <pad>");
                    break;

                case "server":
                    if (parts.Length > 1 && parts[1] == "stop") { ServerConfig.Stop(); _state.Log("Server stopped."); }
                    else { ServerConfig.Start(cfg); _state.Log("Server starting..."); }
                    break;

                case "logs":
                    Process.Start("explorer.exe", Paths.AppData);
                    break;

                case "guard":
                    _focus.Toggle();
                    break;

                default:
                    _state.Log($"unknown command '{verb}' - try: launch, stop, retile, park, assign, server, logs, guard");
                    break;
            }
        }
        catch (Exception ex) { _state.Log($"command failed: {ex.Message}"); }
    }

    /// <summary>Rebuilds instance folders from scratch (Settings -> Rebuild).</summary>
    public async Task RebuildAsync(int players)
    {
        var cfg = _state.Config;
        cfg.EnsureDefaultPlayers(Math.Max(1, players));
        _state.Save();
        await Task.Run(() => Instances.EnsureBuilt(cfg, Math.Max(1, players), _state.Progress(), rebuild: true));
        SetStatus("Instances rebuilt.");
    }

    public async Task LaunchAsync(int players)
    {
        var cfg = _state.Config;
        var blockers = SetupStatus.Blockers(cfg);
        if (blockers.Count > 0)
        {
            SetStatus($"Setup incomplete: {blockers[0]}");
            NavSetup.IsChecked = true;
            return;
        }

        cfg.EnsureDefaultPlayers(players);
        _state.Save();

        SetStatus($"Launching {players} instances...");
        // Put the live dashboard in front immediately. Launcher output, mod health,
        // ports and resource usage now live here instead of in loose console windows.
        NavSession.IsChecked = true;
        try
        {
            await _launcher.LaunchAsync(_state.Progress());
            _logs.Rebuild(cfg);
            if (cfg.Watchdog && !cfg.UseProtoInput) _focus.Start();
            SetStatus(cfg.UseProtoInput
                ? "Session running. Controllers remain isolated when focused."
                : "Session running. Don't click into a game window.");
        }
        catch (Exception ex)
        {
            _state.Log("Launch failed: " + ex.Message);
            SetStatus("Launch failed - see the console.");
        }
    }

    /// <summary>Retile only the game windows that exist right now.</summary>
    private Task RetileRunningAsync()
    {
        _launcher.RetileRunning(_state.Progress());
        return Task.CompletedTask;
    }

    private static UIElement Placeholder(string title, string sub)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.Resources["H1"] });
        panel.Children.Add(new TextBlock { Text = sub, Style = (Style)Application.Current.Resources["Sub"] });
        return panel;
    }
}

/// <summary>Minimal ICommand so a key gesture can invoke a method.</summary>
public sealed class RelayCommand(Action action) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => action();
}
