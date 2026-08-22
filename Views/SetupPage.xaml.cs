using System.Diagnostics;
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
    private readonly Action _openPlay;
    private bool _busy;

    public SetupPage(Func<int, Task> launch, Func<Task> retile, Action openPlay,
        Action openScreens, Action openServer, Action openSettings, Action startTour)
    {
        InitializeComponent();
        _openPlay = openPlay;
        GuideHost.Content = new GuidePage(launch, retile, openScreens, openServer,
            openSettings, ShowPlay, startTour);

        BtnRecheck.Click += (_, _) => Refresh();
        BtnOpenData.Click += (_, _) => Process.Start("explorer.exe", Paths.AppData);
        BtnFixAll.Click += async (_, _) => await FixAllAsync();
        BtnGoPlay.Click += (_, _) => ShowPlay();
        BtnStatusView.Click += (_, _) => ShowStatus();
        BtnGuideView.Click += (_, _) => ShowGuide();

        _state.Logged += OnLogged;
        Unloaded += (_, _) => _state.Logged -= OnLogged;
        foreach (var line in _state.Snapshot().TakeLast(80)) Append(line);

        Refresh();
        if (!_state.Config.OnboardingComplete) ShowGuide();
        else ShowStatus();
    }

    private void ShowPlay() => _openPlay();

    private void ShowStatus()
    {
        ReadinessView.Visibility = Visibility.Visible;
        GuideHost.Visibility = Visibility.Collapsed;
        SetTab(BtnStatusView, true);
        SetTab(BtnGuideView, false);
        Refresh();
    }

    private void ShowGuide()
    {
        ReadinessView.Visibility = Visibility.Collapsed;
        GuideHost.Visibility = Visibility.Visible;
        SetTab(BtnStatusView, false);
        SetTab(BtnGuideView, true);
    }

    private void SetTab(Button button, bool selected)
    {
        button.Background = (Brush)FindResource(selected ? "Accent" : "CardHi");
        button.Foreground = selected ? Brushes.Black : (Brush)FindResource("Fg");
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
            ? $"{ready}/{items.Count} ready  ·  good to launch"
            : $"{ready}/{items.Count} ready  ·  {blockers} blocking";
        LblSummary.Foreground = (Brush)FindResource(blockers == 0 ? "Good" : "Warn");

        var required = items.Where(i => i.Essential).ToList();
        var requiredReady = required.Count(i => i.Ok);
        var percent = required.Count == 0 ? 100 : requiredReady * 100d / required.Count;
        SetupProgress.BeginAnimation(ProgressBar.ValueProperty,
            new DoubleAnimation(SetupProgress.Value, percent, TimeSpan.FromMilliseconds(520))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        LblSetupPercent.Text = $"{percent:0}%";
        LblSetupReadiness.Text = blockers == 0
            ? "All required checks passed. Your saved rig is ready."
            : $"{blockers} required item(s) still need attention.";
        LblSetupHint.Text = blockers == 0
            ? "Setup is healthy. Configure the session and launch from Play."
            : "Use the action beside a red item, or let BeamSplit repair every automatic item in one pass.";
        BtnGoPlay.IsEnabled = blockers == 0;
    }

    private UIElement BuildRow(SetupItem item)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var dot = new Ellipse
        {
            Width = 9, Height = 9, Margin = new Thickness(0, 6, 12, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Fill = (Brush)FindResource(item.Ok ? "Good" : item.Essential ? "Bad" : "Warn")
        };
        if (!item.Ok)
            dot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.35, TimeSpan.FromMilliseconds(900))
            {
                AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            });
        grid.Children.Add(dot);

        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = item.Name, FontSize = 13.5 });
        text.Children.Add(new TextBlock
        {
            Text = item.Detail, FontSize = 11.5, Foreground = (Brush)FindResource("Muted"),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 12, 0)
        });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        if (!item.Ok && !string.IsNullOrEmpty(item.Action))
        {
            var button = new Button
            {
                Content = item.Action, Style = (Style)FindResource("Small"),
                VerticalAlignment = VerticalAlignment.Top, Tag = item.Key
            };
            button.Click += async (_, _) => await FixAsync(item.Key);
            Grid.SetColumn(button, 2);
            grid.Children.Add(button);
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
        try { await SetupRepair.FixAsync(key, _state); }
        catch (Exception ex) { _state.Log($"Fix '{key}' failed: {ex.Message}"); }
        finally
        {
            _busy = false;
            BtnFixAll.IsEnabled = true;
            if (refresh) Refresh();
        }
    }
}
