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
    private readonly List<TourStep> _tourSteps = [];
    private RadioButton? _current;
    private int _tourIndex;
    private bool _launching;
    private bool _relaunching;
    private readonly HashSet<string> _memoryAdviceShown = new(StringComparer.OrdinalIgnoreCase);

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

        _pages[NavSetup] = () => new SetupPage(LaunchAsync, RetileRunningAsync,
            () => NavPlay.IsChecked = true,
            () => NavScreens.IsChecked = true,
            () => NavServer.IsChecked = true,
            () => NavSettings.IsChecked = true,
            StartTour);
        _pages[NavPlay] = () => new PlayPage(LaunchAsync, RetileRunningAsync,
            SwitchMapAsync,
            () => NavSetup.IsChecked = true,
            () => NavScreens.IsChecked = true);
        _pages[NavScreens] = () => new ScreensPage(LaunchAsync, RetileRunningAsync);
        _pages[NavServer] = () => new ServerPage();
        _pages[NavMods] = () => new ModsPage();
        _pages[NavSession] = () => new SessionPage(_session,
            LaunchAsync,
            RelaunchInstanceAsync,
            () => !_launching && !_relaunching,
            RetileRunningAsync,
            () => { _launcher.StopSession(_state.Progress()); _focus.Stop(); },
            () => { _launcher.StopAll(_state.Progress()); _focus.Stop(); });
        _pages[NavSettings] = () => new SettingsPage(RebuildAsync,
            () => PlayCinematicAsync(Math.Max(1, _state.Config.Players.Count)));

        foreach (var nav in _pages.Keys)
            nav.Checked += (s, _) => Navigate((RadioButton)s!);

        _tourSteps.AddRange([
            new TourStep(NavSetup, "01 · SCRUTINEERING", "Know exactly what is ready",
                "Setup owns the first-time walkthrough and the complete dependency checklist. Green items are ready, red items block launch, and amber items are optional recommendations.",
                "Use Fix everything it can after an update or whenever an install path changes."),
            new TourStep(NavPlay, "02 · READY ROOM", "Build the session in one place",
                "Play combines mode, player count, fast screen presets, controller and audio behaviour, frame cap, mods and the final launch button without burying them in separate tabs.",
                "Use a preset for the common layouts, then open Screens only when you want custom placement."),
            new TourStep(NavScreens, "03 · CREW CHIEF", "Put every driver in their seat",
                "Screens maps the real Windows display layout. Split a panel, drag player/controller chips into regions, identify pads, and apply routing or placement to a session that is already running.",
                "Display names—not discovery order—are saved, so unplugging a monitor cannot silently swap player identities."),
            new TourStep(NavServer, "04 · PIT LANE", "Own the shared world",
                "Server holds the local BeamMP race rules: AuthKey, map, player and vehicle limits, port, privacy and server identity. Solo drivers can ignore this entire page.",
                "The server still needs a free BeamMP AuthKey even when every player is on this one PC."),
            new TourStep(NavMods, "05 · LOADOUT", "Bring the good stuff",
                "Mods mounts your normal mod library into every local profile without copying it. Separately choose which ZIPs the BeamMP server distributes as a pack.",
                "The shared library costs no extra storage. Keep client-only extras out of Server sends; those packages are downloaded by every connected player."),
            new TourStep(NavSession, "06 · INSTRUMENT CLUSTER", "Read the rig like a dashboard",
                "Session is the live process monitor. Its only dials are actual system load and RAM; running instances and world sync use clearer status cards. Each driver card exposes state, port, pad, PID, memory, load, mod health and the latest launcher/game signal.",
                "Connected and synced are different: a car can reach its launcher before it has actually appeared in the shared world."),
            new TourStep(NavSettings, "07 · GARAGE", "Tune once, apply everywhere",
                "Settings controls installation paths, frame caps, graphics, input, audio perspective, output devices, portable updates and maintenance. BeamSplit writes the chosen runtime values into every profile before launch.",
                "Local vehicle audio avoids doubled cars on shared speakers. Use All only when each player has a separately routed output.")
        ]);
        BtnTour.Click += (_, _) => StartTour();
        TourOverlay.Next += () => { if (_tourIndex >= _tourSteps.Count - 1) CloseTour(); else ShowTourStep(_tourIndex + 1); };
        TourOverlay.Back += () => ShowTourStep(_tourIndex - 1);
        TourOverlay.Close += CloseTour;

        BtnConsole.Click += (_, _) => ToggleConsole();
        InputBindings.Add(new KeyBinding(new RelayCommand(ToggleConsole),
            new KeyGesture(Key.OemTilde, ModifierKeys.Control)));

        _focus.Changed += () => Dispatcher.BeginInvoke(UpdateStatusDots);

        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) => UpdateStatusDots();
        timer.Start();

        _logs.Rebuild(_state.Config);

        Loaded += async (_, _) =>
        {
            var initial = _state.Config.OnboardingComplete ? NavPlay : NavSetup;
            initial.IsChecked = true;
            Navigate(initial);
            await CheckForUpdatesQuietlyAsync();
        };
        Closed += (_, _) => { _focus.Stop(); _session.Stop(); _logs.Dispose(); _state.Save(); };
    }

    public void SetStatus(string text) => LblStatus.Text = text;

    private void StartTour()
    {
        if (_consoleOpen) ToggleConsole();
        ShowTourStep(0);
    }

    private void ShowTourStep(int index)
    {
        _tourIndex = Math.Clamp(index, 0, _tourSteps.Count - 1);
        var step = _tourSteps[_tourIndex];
        step.Nav.IsChecked = true;
        Dispatcher.BeginInvoke(() => TourOverlay.ShowStep(_tourIndex, _tourSteps.Count,
            step.Eyebrow, step.Title, step.Body, step.Tip), DispatcherPriority.Loaded);
    }

    private void CloseTour()
    {
        TourOverlay.Visibility = Visibility.Collapsed;
        _state.Config.AppTourComplete = true;
        _state.Save();
    }

    private async Task CheckForUpdatesQuietlyAsync()
    {
        var cfg = _state.Config;
        if (!cfg.AutoUpdateCheck || cfg.LastUpdateCheckUtc is DateTime last &&
            DateTime.UtcNow - last.ToUniversalTime() < TimeSpan.FromHours(24)) return;

        try
        {
            var update = await AppUpdater.CheckAsync();
            cfg.LastUpdateCheckUtc = DateTime.UtcNow;
            _state.Save();
            if (update.Available)
            {
                SetStatus($"BeamSplit {update.Latest} is available - open Settings to install it.");
                _state.Log($"Update available: {update.Latest} (verified GitHub release asset).");
            }
        }
        catch (Exception ex)
        {
            _state.Log($"Update check skipped: {ex.Message}");
        }
    }

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
                    await _launcher.RetileRunningAsync(_state.Progress());
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
        if (cfg.SessionEngine == SessionEngine.SingleInstanceExperimental)
        {
            await Task.Run(() => Instances.EnsureSingleBuilt(cfg, _state.Progress(), rebuild: true));
            SetStatus("Single-instance profile rebuilt.");
        }
        else
        {
            await Task.Run(() => Instances.EnsureBuilt(cfg, Math.Max(1, players), _state.Progress(), rebuild: true));
            SetStatus("Instances rebuilt.");
        }
    }

    public async Task LaunchAsync(int players)
    {
        if (_launching) { SetStatus("Launch is already in progress."); return; }
        var cfg = _state.Config;
        var blockers = SetupStatus.Blockers(cfg);
        if (blockers.Count > 0)
        {
            SetStatus($"Setup incomplete: {blockers[0]}");
            NavSetup.IsChecked = true;
            return;
        }

        if (cfg.SessionEngine == SessionEngine.SingleInstanceExperimental)
        {
            var answer = MessageBox.Show(this,
                "Single-instance mode honestly sucks right now and probably won't work correctly on your setup.\n\n" +
                "It may crash, show a black screen, break the menus or map, or give both players the wrong view. " +
                "I'm actively working on it.\n\n" +
                "Use Multi-instance · stable unless you're deliberately testing the experimental engine.\n\n" +
                "Launch single-instance anyway?",
                "Single instance is VERY experimental",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes)
            {
                SetStatus("Experimental launch cancelled. Choose Multi-instance · stable to play.");
                _state.Log("Single-instance launch cancelled at the experimental warning.");
                return;
            }
        }

        cfg.EnsureDefaultPlayers(players);

        var mapPath = cfg.Mode == "BeamMP"
            ? ServerConfig.Read(cfg).GetValueOrDefault("Map", "")
            : "";
        var machine = SystemStats.Capture();
        var advice = MemoryAdvisor.Evaluate(cfg, players, mapPath,
            machine.TotalMemoryMb, Math.Max(0, machine.TotalMemoryMb - machine.UsedMemoryMb));
        if (advice is not null && _memoryAdviceShown.Add(mapPath))
        {
            var dialog = new MemoryAdviceDialog(advice) { Owner = this };
            if (dialog.ShowDialog() == true)
            {
                cfg.LowMemoryGraphics = true;
                _state.Log($"Low-memory graphics enabled for {advice.MapName} on a {advice.TotalMemoryMb / 1024d:0.0} GB system.");
            }
            else
                _state.Log($"Low-memory recommendation declined for {advice.MapName}; continuing with current graphics.");
        }
        _state.Save();

        SetStatus(cfg.SessionEngine == SessionEngine.SingleInstanceExperimental
            ? "Launching one BeamNG instance with two local seats..."
            : $"Launching {players} instances...");
        // Put the live dashboard in front immediately. Launcher output, mod health,
        // ports and resource usage now live here instead of in loose console windows.
        NavSession.IsChecked = true;
        _launching = true;
        try
        {
            // Start the actual parallel launch first. The cinematic hides its slowest
            // early work rather than delaying it; Session telemetry is already alive
            // underneath and is revealed when the split-screen grid resolves.
            // Launcher has unavoidable synchronous sections: instance repair, DLL/mod
            // deployment, process inspection and window calls. Starting an async method
            // on the dispatcher still runs those sections on the dispatcher and freezes
            // the film whenever Windows blocks one of them. Keep the entire pipeline on
            // a worker; LaunchTelemetry is explicitly thread-safe.
            var launchTask = Task.Run(() => _launcher.LaunchAsync(_state.WorkerProgress()));
            if (cfg.LaunchCinematic)
            {
                await PlayCinematicAsync(players, launchTask);
            }
            await launchTask;
            _logs.Rebuild(cfg);
            if (cfg.SessionEngine == SessionEngine.MultiInstance && cfg.Watchdog && !cfg.UseProtoInput) _focus.Start();
            SetStatus(cfg.SessionEngine == SessionEngine.SingleInstanceExperimental
                ? "Shared game running. Pick a Freeroam map; split-screen activates when it loads."
                : cfg.UseProtoInput
                    ? "Session running. Controllers remain isolated when focused."
                    : "Session running. Don't click into a game window.");
        }
        catch (Exception ex)
        {
            _state.Log("Launch failed: " + ex.Message);
            SetStatus("Launch failed - see the console.");
        }
        finally { _launching = false; }
    }

    private async Task RelaunchInstanceAsync(int instance)
    {
        if (_launching || _relaunching)
        {
            SetStatus("Another launch is already in progress.");
            return;
        }

        _relaunching = true;
        SetStatus($"Relaunching Player {instance + 1} ...");
        try
        {
            await Task.Run(() => _launcher.RelaunchInstanceAsync(instance, _state.WorkerProgress()));
            _logs.Rebuild(_state.Config);
            _session.Refresh();
            SetStatus($"Player {instance + 1} relaunched without interrupting the session.");
        }
        catch (Exception ex)
        {
            _state.Log($"P{instance} relaunch failed: {ex.Message}");
            SetStatus($"Player {instance + 1} relaunch failed - see Console.");
        }
        finally { _relaunching = false; }
    }

    private async Task<bool> SwitchMapAsync(string mapPath)
    {
        if (_launching || _relaunching)
        {
            SetStatus("Wait for the current launch before switching maps.");
            return false;
        }

        var cfg = _state.Config;
        if (cfg.Mode != "BeamMP") return false;
        var wasRunning = ServerConfig.IsRunning();
        SetStatus(wasRunning ? "Switching BeamMP map; game instances stay open ..." : "Saving BeamMP map ...");
        try
        {
            await Task.Run(async () =>
            {
                if (wasRunning)
                {
                    ServerConfig.Stop();
                    for (var pass = 0; pass < 30 && ServerConfig.IsRunning(); pass++)
                        await Task.Delay(100);
                }
                if (!ServerConfig.Write(cfg, new Dictionary<string, string> { ["Map"] = mapPath }))
                    throw new InvalidOperationException("The BeamMP server config is unavailable.");
                _state.Log($"Server map set to {mapPath}");
                if (wasRunning)
                {
                    ServerConfig.Start(cfg);
                    await Task.Delay(1500);
                    if (!ServerConfig.IsRunning())
                        throw new InvalidOperationException("The BeamMP server did not restart.");
                }
            });
            _session.Refresh();
            SetStatus(wasRunning
                ? cfg.AutoJoinBeamMp
                    ? "Map switched. Running games are reconnecting to the restarted server."
                    : "Map switched. Games stayed open; reconnect manually because Auto Join is off."
                : "Map saved for the next BeamMP launch.");
            return true;
        }
        catch (Exception ex)
        {
            _state.Log("Map switch failed: " + ex.Message);
            SetStatus("Map switch failed - see Console.");
            return false;
        }
    }

    private async Task PlayCinematicAsync(int players, Task? launchTask = null)
    {
        var previousState = WindowState;
        var previousStyle = WindowStyle;
        var previousResize = ResizeMode;
        var previousTopmost = Topmost;
        var previousBounds = RestoreBounds;
        try
        {
            // This is a real launch film, not a maximized app page: remove the title
            // bar and keep it above the game windows that are appearing behind it.
            WindowState = System.Windows.WindowState.Normal;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            Topmost = true;
            WindowState = System.Windows.WindowState.Maximized;
            Activate();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            await LaunchOverlay.PlayAsync(players, launchTask);
        }
        finally
        {
            LaunchOverlay.Abort();
            Topmost = previousTopmost;
            WindowStyle = previousStyle;
            ResizeMode = previousResize;
            WindowState = previousState;
            if (previousState == System.Windows.WindowState.Normal && !previousBounds.IsEmpty)
            {
                Left = previousBounds.Left;
                Top = previousBounds.Top;
                Width = previousBounds.Width;
                Height = previousBounds.Height;
            }
        }
    }

    /// <summary>Retile only the game windows that exist right now.</summary>
    private Task RetileRunningAsync() => _launcher.RetileRunningAsync(_state.Progress());

    private static UIElement Placeholder(string title, string sub)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.Resources["H1"] });
        panel.Children.Add(new TextBlock { Text = sub, Style = (Style)Application.Current.Resources["Sub"] });
        return panel;
    }
}

internal sealed record TourStep(RadioButton Nav, string Eyebrow, string Title, string Body, string Tip);

/// <summary>Minimal ICommand so a key gesture can invoke a method.</summary>
public sealed class RelayCommand(Action action) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => action();
}
