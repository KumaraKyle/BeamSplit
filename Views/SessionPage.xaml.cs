using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using BeamSplit.Core;

namespace BeamSplit.Views;

public partial class SessionPage : UserControl
{
    private readonly AppState _state = AppState.Current;
    private readonly SessionMonitor _monitor;
    private readonly Func<int, Task> _launch;
    private readonly Func<int, Task> _relaunch;
    private readonly Func<bool> _canRelaunch;
    private readonly Func<Task> _retile;
    private readonly Action _stopSession;
    private readonly Action _stopAll;
    private readonly HashSet<int> _relaunching = [];

    public SessionPage(SessionMonitor monitor, Func<int, Task> launch, Func<int, Task> relaunch,
        Func<bool> canRelaunch, Func<Task> retile,
        Action stopSession, Action stopAll)
    {
        InitializeComponent();
        _monitor = monitor;
        _launch = launch;
        _relaunch = relaunch;
        _canRelaunch = canRelaunch;
        _retile = retile;
        _stopSession = stopSession;
        _stopAll = stopAll;

        BtnLaunch.Click += async (_, _) => await _launch(Math.Max(2, _state.Config.Players.Count));
        BtnRetile.Click += async (_, _) => await _retile();
        BtnStopSession.Click += (_, _) => _stopSession();
        BtnStopAll.Click += (_, _) => _stopAll();
        BtnServerToggle.Click += (_, _) =>
        {
            if (ServerConfig.IsRunning()) ServerConfig.Stop();
            else ServerConfig.Start(_state.Config);
            _monitor.Refresh();
        };
        BtnServerLog.Click += (_, _) =>
        {
            var log = ServerConfig.LogPath(_state.Config);
            if (log != null && File.Exists(log)) Process.Start("explorer.exe", $"/select,\"{log}\"");
        };

        _monitor.Updated += OnUpdated;
        Unloaded += (_, _) => _monitor.Updated -= OnUpdated;

        _monitor.Refresh();
    }

    private void OnUpdated() => Dispatcher.BeginInvoke(Render);

    private void Render()
    {
        var machine = SystemStats.Capture();
        var processRows = _monitor.Items.Where(i => i.GamePid != 0).GroupBy(i => i.GamePid).Select(g => g.First()).ToList();
        var totalGameMemory = processRows.Sum(i => i.MemoryMb);
        var totalGameCpu = processRows.Sum(i => i.CpuPercent);
        var running = _monitor.Items.Count(i => i.GamePid != 0);
        var ready = _monitor.Items.Count(i => i.State is InstanceState.Synced or InstanceState.GameRunning);
        var count = Math.Max(1, _monitor.Items.Count);
        var memoryPercent = machine.TotalMemoryMb > 0 ? machine.UsedMemoryMb * 100d / machine.TotalMemoryMb : 0;
        GaugeCpu.Value = machine.SystemLoadPercent;
        GaugeCpu.ValueText = $"{machine.SystemLoadPercent:0}%";
        GaugeRam.Value = memoryPercent;
        GaugeRam.ValueText = machine.TotalMemoryMb > 0 ? $"{machine.UsedMemoryMb / 1024d:0.0}G" : $"{totalGameMemory / 1024d:0.0}G";
        LblInstancesValue.Text = $"{running} / {count}";
        var single = _state.Config.SessionEngine == SessionEngine.SingleInstanceExperimental;
        LblRunningTitle.Text = single ? "ACTIVE LOCAL SEATS" : "RUNNING INSTANCES";
        LblInstancesDetail.Text = running == count
            ? single ? "Both seats share one BeamNG process." : "All configured game windows are running."
            : single ? $"{count - running} seat(s) waiting." : $"{count - running} instance(s) waiting.";
        LblSyncValue.Text = $"{ready} / {count}";
        LblSyncDetail.Text = ready == count ? "Every running driver is ready." : "Waiting for game or BeamMP synchronization.";
        LblDashClock.Text = DateTime.Now.ToString("HH:mm:ss");
        LblMachineStrip.Text = $"{machine.Cpu}  ·  {machine.Threads} threads  ·  {machine.Gpu}";
        var displayCount = Native.GetMonitors().Count;
        LblDisplayStrip.Text = $"Games {totalGameCpu:0}% CPU  ·  {totalGameMemory:N0} MB RAM  ·  {displayCount} display{(displayCount == 1 ? "" : "s")}";

        var s = _monitor.Server;
        var beamMp = !single && _state.Config.Mode == "BeamMP";

        DotServer.Fill = (Brush)FindResource(!beamMp ? "Faint" : s.Running && s.Listening ? "Good" : s.Running ? "Warn" : "Faint");
        SetLamp(LampServer, !beamMp || s.Running && s.Listening, beamMp && s.Running && !s.Listening);
        SetLamp(LampInput, single || _state.Config.UseProtoInput && NativeAssets.ProtoInputReady, false);
        SetLamp(LampWindows, running == count, running > 0 && running < count);
        LblServerTitle.Text = beamMp ? "BeamMP server" : "BeamMP server (not used in Solo)";
        BtnServerToggle.Content = s.Running ? "Stop" : "Start";

        LblServerDetail.Text = !s.Running
            ? (s.AuthKey ? "offline" : "offline - no AuthKey set, it will refuse to start")
            : $"port {s.Port} {(s.Listening ? "listening" : "NOT listening")}   -   {System.IO.Path.GetFileName(s.Map.TrimEnd('/'))}   -   up {Format(s.Uptime)}";

        PlayerList.Items.Clear();
        foreach (var p in s.Players)
            PlayerList.Items.Add(new TextBlock
            {
                Text = "  " + p,
                FontSize = 11.5,
                Foreground = (Brush)FindResource("Good")
            });

        Cards.Items.Clear();
        foreach (var inst in _monitor.Items) Cards.Items.Add(BuildCard(inst));
    }

    private void SetLamp(Border lamp, bool good, bool warning)
    {
        var color = good ? Color.FromRgb(28, 104, 66) : warning ? Color.FromRgb(112, 75, 24) : Color.FromRgb(42, 45, 54);
        lamp.Background = new SolidColorBrush(color);
        if (lamp.Child is TextBlock text)
            text.Foreground = (Brush)FindResource(good ? "Good" : warning ? "Warn" : "Faint");
    }

    private static string Format(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m" : t.TotalMinutes >= 1 ? $"{t.Minutes}m" : $"{t.Seconds}s";

    private UIElement BuildCard(InstanceStatus st)
    {
        var brush = st.State switch
        {
            InstanceState.Synced or InstanceState.GameRunning => "Good",
            InstanceState.Error => "Bad",
            InstanceState.Idle => "Faint",
            _ => "Warn"
        };

        var border = new Border
        {
            Style = (Style)FindResource("CardStyle"),
            Padding = new Thickness(16)
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var dot = new Ellipse
        {
            Width = 10,
            Height = 10,
            Margin = new Thickness(0, 5, 12, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Fill = (Brush)FindResource(brush)
        };
        // pulse while something is still in flight
        if (st.State is InstanceState.Launching or InstanceState.WaitingForLauncher or InstanceState.Connected or InstanceState.Building)
        {
            dot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.3, TimeSpan.FromMilliseconds(800))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase()
            });
        }
        Grid.SetColumn(dot, 0);
        grid.Children.Add(dot);

        var text = new StackPanel();
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(new TextBlock { Text = $"Player {st.Index + 1}", Style = (Style)FindResource("H2") });
        head.Children.Add(new TextBlock
        {
            Text = "  " + st.StateText,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)FindResource(brush)
        });
        text.Children.Add(head);

        text.Children.Add(new TextBlock
        {
            Text = st.Detail,
            FontSize = 11.5,
            Foreground = (Brush)FindResource("Muted"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 12, 0)
        });

        var facts = new List<string>();
        if (st.Pad >= 0) facts.Add($"pad {st.Pad}");
        if (!string.IsNullOrEmpty(st.Monitor)) facts.Add(st.Monitor);
        if (_state.Config.Mode == "BeamMP") facts.Add($"port {st.Port}{(st.PortListening ? "" : " (closed)")}");
        if (st.GamePid != 0) facts.Add($"pid {st.GamePid}");
        if (st.GamePid != 0) facts.Add($"CPU {st.CpuPercent:0.0}%");
        if (st.MemoryMb > 0) facts.Add($"RAM {st.MemoryMb:N0} MB");
        text.Children.Add(new TextBlock
        {
            Text = string.Join("   ·   ", facts),
            FontSize = 11,
            Foreground = (Brush)FindResource("Faint"),
            Margin = new Thickness(0, 5, 0, 0)
        });

        if (st.GamePid != 0)
        {
            var meters = new UniformGrid { Columns = 2, Margin = new Thickness(0, 9, 12, 0) };
            meters.Children.Add(MetricMeter("ENGINE LOAD", st.CpuPercent, 100, $"{st.CpuPercent:0.0}%", (Brush)FindResource("Accent")));
            meters.Children.Add(MetricMeter("WORKING SET", st.MemoryMb, 6144, $"{st.MemoryMb:N0} MB", new SolidColorBrush(Color.FromRgb(81, 214, 232))));
            text.Children.Add(meters);
        }

        text.Children.Add(new TextBlock
        {
            Text = "BEAMMP  " + st.ModState,
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource(st.ModOk ? "Good" : "Bad"),
            Margin = new Thickness(0, 7, 0, 0)
        });

        if (!string.IsNullOrWhiteSpace(st.LastLine) || !string.IsNullOrWhiteSpace(st.LauncherLine))
        {
            var logLines = new List<string>();
            if (!string.IsNullOrWhiteSpace(st.LauncherLine))
                logLines.Add("MP    " + Trim(st.LauncherLine, 100));
            if (!string.IsNullOrWhiteSpace(st.LastLine))
                logLines.Add("GAME  " + Trim(st.LastLine, 100));
            var logBox = new Border
            {
                Background = (Brush)FindResource("Bg"),
                BorderBrush = (Brush)FindResource("Line"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 5, 8, 5),
                Margin = new Thickness(0, 7, 12, 0),
                Child = new TextBlock
                {
                    Text = string.Join(Environment.NewLine, logLines),
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 10,
                    Foreground = (Brush)FindResource("Faint"),
                    TextTrimming = TextTrimming.CharacterEllipsis
                }
            };
            text.Children.Add(logBox);
        }

        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        if (_state.Config.SessionEngine == SessionEngine.MultiInstance &&
            (st.State is InstanceState.Idle or InstanceState.WaitingForLauncher or InstanceState.Error) &&
            !_relaunching.Contains(st.Index) && _canRelaunch())
        {
            var relaunch = new Button
            {
                Content = "Relaunch instance",
                Style = (Style)FindResource("Primary"),
                Margin = new Thickness(0, 0, 6, 0),
                ToolTip = "Restart only this player's game and launcher, preserving the rest of the session"
            };
            relaunch.Click += async (_, _) =>
            {
                if (!_relaunching.Add(st.Index)) return;
                relaunch.IsEnabled = false;
                relaunch.Content = "Relaunching…";
                try { await _relaunch(st.Index); }
                finally
                {
                    _relaunching.Remove(st.Index);
                    _monitor.Refresh();
                }
            };
            actions.Children.Add(relaunch);
        }
        var openLog = new Button { Content = "Log", Style = (Style)FindResource("Small"), Margin = new Thickness(0, 0, 6, 0) };
        openLog.Click += (_, _) =>
        {
            var index = _state.Config.SessionEngine == SessionEngine.SingleInstanceExperimental
                ? Instances.SingleInstanceIndex : st.Index;
            var log = System.IO.Path.Combine(Instances.CurrentProfile(_state.Config, index), "beamng.log");
            if (File.Exists(log)) Process.Start("explorer.exe", $"/select,\"{log}\"");
        };
        actions.Children.Add(openLog);

        var folder = new Button { Content = "Folder", Style = (Style)FindResource("Small") };
        folder.Click += (_, _) =>
        {
            var index = _state.Config.SessionEngine == SessionEngine.SingleInstanceExperimental
                ? Instances.SingleInstanceIndex : st.Index;
            var dir = Instances.InstanceDir(_state.Config, index);
            if (Directory.Exists(dir)) Process.Start("explorer.exe", dir);
        };
        actions.Children.Add(folder);

        Grid.SetColumn(actions, 2);
        grid.Children.Add(actions);

        border.Child = grid;
        return border;
    }

    private UIElement MetricMeter(string label, double value, double max, string valueText, Brush color)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        var head = new Grid();
        head.Children.Add(new TextBlock { Text = label, FontSize = 9, FontWeight = FontWeights.Bold, Foreground = (Brush)FindResource("Faint") });
        head.Children.Add(new TextBlock { Text = valueText, FontSize = 10, FontFamily = new FontFamily("Consolas"), Foreground = color, HorizontalAlignment = HorizontalAlignment.Right });
        panel.Children.Add(head);
        panel.Children.Add(new ProgressBar
        {
            Style = (Style)FindResource("GlowProgress"), Height = 5, Maximum = max,
            Value = Math.Clamp(value, 0, max), Foreground = color, Margin = new Thickness(0, 4, 0, 0)
        });
        return panel;
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max] + "...";
}
