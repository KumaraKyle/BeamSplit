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

    public SetupPage(Func<int, Task> launch, Func<Task> retile, Action openScreens,
        Action openServer, Action openSettings, Action startTour)
    {
        InitializeComponent();
        _launch = launch;
        _customize = openScreens;

        GuideHost.Content = new GuidePage(launch, retile, openScreens, openServer,
            openSettings, ShowQuickPlay, startTour);

        BtnRecheck.Click += (_, _) => Refresh();
        BtnOpenData.Click += (_, _) => Process.Start("explorer.exe", Paths.AppData);
        BtnFixAll.Click += async (_, _) => await FixAllAsync();
        BtnCustomize.Click += (_, _) => _customize();
        BtnGuide.Click += (_, _) => ShowGuide();
        BtnPlayView.Click += (_, _) => ShowQuickPlay();
        BtnGuideView.Click += (_, _) => ShowGuide();
        BtnQuickLaunch.Click += async (_, _) => await QuickLaunchAsync();
        CbQuickPlayers.SelectedIndex = Math.Clamp(Math.Max(1, _state.Config.Players.Count) - 1, 0, 3);
        CbQuickMode.SelectedIndex = _state.Config.Mode == "Solo" ? 1 : 0;
        CbQuickMode.SelectionChanged += (_, _) =>
        {
            _state.Config.Mode = CbQuickMode.SelectedIndex == 1 ? "Solo" : "BeamMP";
            _state.Save();
            Refresh();
        };

        _state.Logged += OnLogged;
        Unloaded += (_, _) => _state.Logged -= OnLogged;

        foreach (var l in _state.Snapshot().TakeLast(80)) Append(l);
        Refresh();
        if (!_state.Config.OnboardingComplete) ShowGuide();
    }

    private void ShowQuickPlay()
    {
        QuickPlayView.Visibility = Visibility.Visible;
        GuideHost.Visibility = Visibility.Collapsed;
        BtnPlayView.Background = (Brush)FindResource("Accent");
        BtnPlayView.Foreground = Brushes.Black;
        BtnGuideView.Background = (Brush)FindResource("CardHi");
        BtnGuideView.Foreground = (Brush)FindResource("Fg");
        Refresh();
    }

    private void ShowGuide()
    {
        QuickPlayView.Visibility = Visibility.Collapsed;
        GuideHost.Visibility = Visibility.Visible;
        BtnGuideView.Background = (Brush)FindResource("Accent");
        BtnGuideView.Foreground = Brushes.Black;
        BtnPlayView.Background = (Brush)FindResource("CardHi");
        BtnPlayView.Foreground = (Brush)FindResource("Fg");
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

        var applicable = items.Where(i => i.Essential).ToList();
        var essentialReady = applicable.Count(i => i.Ok);
        var percent = applicable.Count == 0 ? 100 : essentialReady * 100d / applicable.Count;
        QuickProgress.BeginAnimation(ProgressBar.ValueProperty,
            new DoubleAnimation(QuickProgress.Value, percent, TimeSpan.FromMilliseconds(520))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        LblQuickPercent.Text = $"{percent:0}%";
        LblQuickReadiness.Text = blockers == 0
            ? "All required checks passed. Instances will start together."
            : $"{blockers} blocking item(s). Launch will repair what it can first.";
        LblQuickHint.Text = blockers == 0
            ? "Your saved screen layout, controller routes, audio perspective, and frame cap will be applied before launch."
            : "BeamSplit can repair most missing pieces automatically; AuthKey and ambiguous game installs still need your choice.";
        BtnQuickLaunch.Content = blockers == 0 ? $"Launch {CbQuickPlayers.SelectedIndex + 1} players" : "Repair & launch";
        LblQuickModePill.Text = _state.Config.Mode;
        var pads = Enumerable.Range(0, 4).Count(i => Native.PadConnected((uint)i));
        LblQuickHardwarePill.Text = $"{Native.GetMonitors().Count} displays · {pads} pads";
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
        foreach (var key in SetupRepair.AutomaticKeys)
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
            await SetupRepair.FixAsync(key, _state);
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
