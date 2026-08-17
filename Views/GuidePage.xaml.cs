using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using BeamSplit.Core;

namespace BeamSplit.Views;

public partial class GuidePage : UserControl
{
    private readonly AppState _state = AppState.Current;
    private readonly Func<int, Task> _launch;
    private readonly Func<Task> _retile;
    private readonly Action _openScreens;
    private readonly Action _openServer;
    private readonly Action _openSettings;
    private readonly Action _finish;
    private readonly Action _startTour;
    private readonly FrameworkElement[] _steps;
    private readonly Border[] _rails;
    private int _step;
    private bool _busy;

    public GuidePage(Func<int, Task> launch, Func<Task> retile, Action openScreens,
        Action openServer, Action openSettings, Action finish, Action startTour)
    {
        InitializeComponent();
        _launch = launch;
        _retile = retile;
        _openScreens = openScreens;
        _openServer = openServer;
        _openSettings = openSettings;
        _finish = finish;
        _startTour = startTour;
        _steps = [Step0, Step1, Step2, Step3, Step4];
        _rails = [Rail0, Rail1, Rail2, Rail3, Rail4];

        CbGuideMode.SelectedIndex = _state.Config.Mode == "Solo" ? 1 : 0;
        CbGuidePlayers.SelectedIndex = Math.Clamp(Math.Max(1, _state.Config.Players.Count) - 1, 0, 3);
        CbGuideAudio.SelectedIndex = _state.Config.AudioMixMode switch { "All" => 1, "P0Only" => 2, _ => 0 };

        BtnGuideBack.Click += (_, _) => ShowStep(_step - 1);
        BtnGuideNext.Click += (_, _) => { SaveChoices(); ShowStep(_step + 1); };
        BtnGuideRepair.Click += async (_, _) => await RepairAsync();
        BtnGuideRecheck.Click += (_, _) => RefreshSetup();
        BtnLayoutAuto.Click += async (_, _) => await ApplyLayoutAsync("auto");
        BtnLayoutStacked.Click += async (_, _) => await ApplyLayoutAsync("stacked");
        BtnLayoutSide.Click += async (_, _) => await ApplyLayoutAsync("side");
        BtnOpenScreens.Click += (_, _) => { SaveChoices(); _openScreens(); };
        BtnOpenServer.Click += (_, _) => { SaveChoices(); _openServer(); };
        BtnOpenSettings.Click += (_, _) => { SaveChoices(); _openSettings(); };
        BtnGuideLaunch.Click += async (_, _) => await CompleteAsync(launch: true);
        BtnFinishGuide.Click += async (_, _) => await CompleteAsync(launch: false);
        BtnTourGuide.Click += (_, _) =>
        {
            SaveChoices();
            _state.Config.OnboardingComplete = true;
            _state.Save();
            _startTour();
        };
        CbGuideMode.SelectionChanged += (_, _) => { if (IsLoaded) SaveChoices(); };
        CbGuideAudio.SelectionChanged += (_, _) => { if (IsLoaded) SaveChoices(); };

        _step = Math.Clamp(_state.Config.OnboardingStep, 0, 4);
        Loaded += (_, _) => ShowStep(_step, animate: false);
    }

    private void SaveChoices()
    {
        var cfg = _state.Config;
        cfg.Mode = CbGuideMode.SelectedIndex == 1 ? "Solo" : "BeamMP";
        cfg.AudioMixMode = CbGuideAudio.SelectedIndex switch { 1 => "All", 2 => "P0Only", _ => "LocalVehicle" };
        var players = CbGuidePlayers.SelectedIndex + 1;
        cfg.EnsureDefaultPlayers(players);
        cfg.OnboardingStep = _step;
        _state.Save();
    }

    private void ShowStep(int requested, bool animate = true)
    {
        _step = Math.Clamp(requested, 0, 4);
        for (var i = 0; i < _steps.Length; i++) _steps[i].Visibility = i == _step ? Visibility.Visible : Visibility.Collapsed;
        for (var i = 0; i < _rails.Length; i++)
        {
            _rails[i].Background = (Brush)FindResource(i == _step ? "Accent" : i < _step ? "CardHi" : "Line");
            if (_rails[i].Child is TextBlock text)
                text.Foreground = i == _step ? Brushes.Black : (Brush)FindResource(i < _step ? "Fg" : "Muted");
        }

        _state.Config.OnboardingStep = _step;
        _state.Save();
        LblStep.Text = $"Step {_step + 1} of 5";
        AnimateValue(WizardProgress, (_step + 1) * 20);
        BtnGuideBack.IsEnabled = _step > 0;
        BtnGuideNext.Visibility = _step == 4 ? Visibility.Collapsed : Visibility.Visible;

        if (_step == 1) RefreshSetup();
        if (_step == 2) RefreshMonitorPreview();
        if (_step == 3) RefreshSessionStep();
        if (_step == 4) RefreshReady();

        if (animate)
        {
            StepHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)));
            ((TranslateTransform)StepHost.RenderTransform).BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(330))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
    }

    private void RefreshSetup()
    {
        var items = SetupStatus.Evaluate(_state.Config);
        GuideSetupItems.Items.Clear();
        foreach (var item in items.Where(i => i.Essential || i.Key is "protoinput" or "devreorder"))
            GuideSetupItems.Items.Add(StatusRow(item));
        var applicable = items.Where(i => i.Essential).ToList();
        var ready = applicable.Count(i => i.Ok);
        var percent = applicable.Count == 0 ? 100 : ready * 100d / applicable.Count;
        AnimateValue(SetupProgress, percent);
        LblGuidePercent.Text = $"{percent:0}%";
        LblGuideSetup.Text = applicable.All(i => i.Ok)
            ? "Everything required for this session mode is ready."
            : $"{applicable.Count - ready} required item(s) still need attention.";
    }

    private UIElement StatusRow(SetupItem item)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 9) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var dot = new Ellipse { Width = 8, Height = 8, Margin = new Thickness(0, 5, 11, 0), VerticalAlignment = VerticalAlignment.Top,
            Fill = (Brush)FindResource(item.Ok ? "Good" : item.Essential ? "Bad" : "Warn") };
        grid.Children.Add(dot);
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = item.Name, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = item.Detail, Foreground = (Brush)FindResource("Muted"), FontSize = 11.5, TextWrapping = TextWrapping.Wrap });
        Grid.SetColumn(panel, 1); grid.Children.Add(panel);
        return grid;
    }

    private async Task RepairAsync()
    {
        if (_busy) return;
        _busy = true;
        BtnGuideRepair.IsEnabled = false;
        try
        {
            await SetupRepair.RepairAutomaticAsync(_state, (index, name) => Dispatcher.Invoke(() =>
            {
                LblGuideSetup.Text = index < SetupRepair.AutomaticKeys.Length ? $"Working on {name}…" : name;
                AnimateValue(SetupProgress, index * 100d / SetupRepair.AutomaticKeys.Length);
            }));
        }
        catch (Exception ex) { _state.Log("Guided repair failed: " + ex.Message); }
        finally { _busy = false; BtnGuideRepair.IsEnabled = true; RefreshSetup(); }
    }

    private async Task ApplyLayoutAsync(string preset)
    {
        SaveChoices();
        var cfg = _state.Config;
        var count = CbGuidePlayers.SelectedIndex + 1;
        var monitors = Native.GetMonitors();
        if (monitors.Count == 0) return;
        cfg.Players.Clear();

        if (preset == "auto") cfg.EnsureDefaultPlayers(count);
        else
        {
            var primary = monitors.FirstOrDefault(m => m.Primary, monitors[0]);
            var split = count > 2 ? SplitMode.FourGrid : preset == "stacked" ? SplitMode.TwoStacked : SplitMode.TwoSideBySide;
            for (var i = 0; i < count; i++)
                cfg.Players.Add(new PlayerSlot { Index = i, MonitorDevice = primary.DeviceName, Split = split, Region = i, Pad = i });
        }
        _state.Save();
        RefreshMonitorPreview();
        await _retile();
    }

    private void RefreshMonitorPreview()
    {
        MonitorPreview.Children.Clear();
        foreach (var monitor in Native.GetMonitors())
        {
            var count = _state.Config.Players.Count(p => p.MonitorDevice.Equals(monitor.DeviceName, StringComparison.OrdinalIgnoreCase));
            var card = new Border { Style = (Style)FindResource("SoftCard"), Width = 180, Height = 105, Margin = new Thickness(0, 0, 10, 10),
                BorderBrush = (Brush)FindResource(count > 0 ? "Accent" : "Line") };
            card.Child = new StackPanel { Children =
            {
                new TextBlock { Text = monitor.DeviceName.Replace(@"\\.\", ""), FontWeight = FontWeights.SemiBold },
                new TextBlock { Text = $"{monitor.Width} × {monitor.Height}", Foreground = (Brush)FindResource("Muted"), Margin = new Thickness(0,3,0,10) },
                new TextBlock { Text = count == 0 ? "unused" : count == 1 ? "1 player" : $"{count} players", Foreground = (Brush)FindResource(count > 0 ? "Good" : "Faint") }
            }};
            MonitorPreview.Children.Add(card);
        }
        var players = _state.Config.Players.Count;
        LblLayout.Text = players == 0 ? "Choose a preset, or open the full designer." : $"{players} player(s) assigned. Pad numbers follow player numbers by default.";
    }

    private void RefreshSessionStep()
    {
        var beamMp = _state.Config.Mode == "BeamMP";
        LblGuideServer.Text = !beamMp ? "Not used in Solo mode. Each player gets an independent world."
            : ServerConfig.HasAuthKey(_state.Config) ? "Server and AuthKey are ready."
            : "The server needs a free AuthKey before shared-world play can start.";
        BtnOpenServer.IsEnabled = beamMp;
    }

    private void RefreshReady()
    {
        SaveChoices();
        var blockers = SetupStatus.Blockers(_state.Config);
        var players = Math.Max(1, _state.Config.Players.Count);
        LblReadySummary.Text = blockers.Count == 0
            ? $"{players} player(s) · {_state.Config.Mode} · {_state.Config.AudioMixMode} audio. BeamSplit will start every player in parallel and stabilize their windows."
            : $"Almost there: {blockers[0]}. You can return to Install check or finish and fix it from Play.";
        BtnGuideLaunch.IsEnabled = blockers.Count == 0;
    }

    private async Task CompleteAsync(bool launch)
    {
        SaveChoices();
        _state.Config.OnboardingComplete = true;
        _state.Config.OnboardingStep = 4;
        _state.Save();
        if (launch) await _launch(Math.Max(1, _state.Config.Players.Count));
        else _finish();
    }

    private static void AnimateValue(ProgressBar bar, double value) =>
        bar.BeginAnimation(ProgressBar.ValueProperty,
            new DoubleAnimation(bar.Value, value, TimeSpan.FromMilliseconds(520))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
}
