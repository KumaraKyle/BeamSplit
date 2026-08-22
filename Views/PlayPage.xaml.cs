using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using BeamSplit.Core;

namespace BeamSplit.Views;

public partial class PlayPage : UserControl
{
    private readonly AppState _state = AppState.Current;
    private readonly Func<int, Task> _launch;
    private readonly Func<Task> _retile;
    private readonly Func<string, Task<bool>> _switchMap;
    private readonly Action _openSetup;
    private readonly Action _openScreens;
    private bool _loading;
    private bool _launching;
    private bool _mapsLoaded;
    private string _selectedMap = "";
    private string _activeMap = "";
    private readonly Dictionary<string, Border> _mapCards = new(StringComparer.OrdinalIgnoreCase);

    public PlayPage(Func<int, Task> launch, Func<Task> retile, Func<string, Task<bool>> switchMap,
        Action openSetup, Action openScreens)
    {
        InitializeComponent();
        _launch = launch;
        _retile = retile;
        _switchMap = switchMap;
        _openSetup = openSetup;
        _openScreens = openScreens;

        BtnLaunch.Click += async (_, _) => await LaunchAsync();
        BtnOpenSetup.Click += (_, _) => _openSetup();
        BtnOpenScreens.Click += (_, _) => _openScreens();
        BtnConfigureControllers.Click += (_, _) => _openScreens();
        BtnApplyPreset.Click += async (_, _) => await ApplyPresetAsync();
        BtnSwitchMap.Click += async (_, _) => await SwitchMapAsync();
        BtnMapPrevious.Click += (_, _) => ScrollMaps(-1);
        BtnMapNext.Click += (_, _) => ScrollMaps(1);
        MapScroller.PreviewMouseWheel += (_, e) =>
        {
            if (MapScroller.ScrollableWidth <= 0) return;
            MapScroller.ScrollToHorizontalOffset(Math.Clamp(
                MapScroller.HorizontalOffset - e.Delta * 0.75, 0, MapScroller.ScrollableWidth));
            e.Handled = true;
        };
        MapScroller.ScrollChanged += (_, _) => UpdateMapScrollButtons();
        CbMode.SelectionChanged += (_, _) => SaveSessionChoices();
        CbEngine.SelectionChanged += (_, _) => SaveSessionChoices();
        CbPlayers.SelectionChanged += (_, _) => SaveSessionChoices();
        CbAudioMix.SelectionChanged += (_, _) => SaveOptions();
        foreach (var check in new[] { ChkBorderless, ChkIsolate, ChkProtoInput, ChkPlayerMods,
                                      ChkCinematic, ChkAutoJoin, ChkAudioBackground, ChkLowMemory })
        {
            check.Checked += (_, _) => SaveOptions();
            check.Unchecked += (_, _) => SaveOptions();
        }
        TxtFrameLimit.LostFocus += (_, _) => SaveOptions();
        Loaded += async (_, _) =>
        {
            LoadConfig();
            if (!_mapsLoaded) await LoadMapsAsync();
        };
    }

    private void LoadConfig()
    {
        _loading = true;
        var cfg = _state.Config;
        CbEngine.SelectedIndex = cfg.SessionEngine == SessionEngine.SingleInstanceExperimental ? 1 : 0;
        CbMode.SelectedIndex = cfg.Mode == "Solo" ? 1 : 0;
        CbPlayers.SelectedIndex = Math.Clamp(Math.Max(1, cfg.Players.Count) - 1, 0, 3);
        CbPreset.SelectedIndex = 0;
        ChkBorderless.IsChecked = cfg.Borderless;
        ChkIsolate.IsChecked = cfg.Isolate;
        ChkProtoInput.IsChecked = cfg.UseProtoInput;
        ChkPlayerMods.IsChecked = cfg.UsePlayerMods;
        ChkLowMemory.IsChecked = cfg.LowMemoryGraphics;
        ChkCinematic.IsChecked = cfg.LaunchCinematic;
        ChkAutoJoin.IsChecked = cfg.AutoJoinBeamMp;
        ChkAudioBackground.IsChecked = cfg.AudioInBackground;
        TxtFrameLimit.Text = cfg.FrameLimit.ToString();
        CbAudioMix.SelectedIndex = cfg.AudioMixMode switch { "All" => 1, "P0Only" => 2, _ => 0 };
        _loading = false;
        RefreshSummary();
        RefreshMapState();
    }

    private void SaveSessionChoices()
    {
        if (_loading || CbPlayers.SelectedIndex < 0) return;
        var cfg = _state.Config;
        cfg.SessionEngine = CbEngine.SelectedIndex == 1
            ? SessionEngine.SingleInstanceExperimental : SessionEngine.MultiInstance;
        if (cfg.SessionEngine == SessionEngine.SingleInstanceExperimental)
        {
            cfg.Mode = "Solo";
            cfg.EnsureDefaultPlayers(2);
            _loading = true;
            CbMode.SelectedIndex = 1;
            CbPlayers.SelectedIndex = 1;
            _loading = false;
        }
        else
        {
            cfg.Mode = CbMode.SelectedIndex == 1 ? "Solo" : "BeamMP";
            cfg.EnsureDefaultPlayers(CbPlayers.SelectedIndex + 1);
        }
        _state.Save();
        RefreshSummary();
        RefreshMapState();
    }

    private void SaveOptions()
    {
        if (_loading) return;
        var cfg = _state.Config;
        cfg.Borderless = ChkBorderless.IsChecked == true;
        cfg.Isolate = ChkIsolate.IsChecked == true;
        cfg.UseProtoInput = ChkProtoInput.IsChecked == true;
        cfg.UsePlayerMods = ChkPlayerMods.IsChecked == true;
        cfg.LowMemoryGraphics = ChkLowMemory.IsChecked == true;
        cfg.LaunchCinematic = ChkCinematic.IsChecked == true;
        cfg.AutoJoinBeamMp = ChkAutoJoin.IsChecked == true;
        cfg.AudioInBackground = ChkAudioBackground.IsChecked == true;
        cfg.AudioMixMode = CbAudioMix.SelectedIndex switch { 1 => "All", 2 => "P0Only", _ => "LocalVehicle" };
        if (int.TryParse(TxtFrameLimit.Text, out var fps)) cfg.FrameLimit = Math.Clamp(fps, 15, 360);
        TxtFrameLimit.Text = cfg.FrameLimit.ToString();
        _state.Save();
        RefreshSummary();
    }

    private async Task ApplyPresetAsync()
    {
        SaveSessionChoices();
        var cfg = _state.Config;
        var count = CbPlayers.SelectedIndex + 1;
        var monitors = Native.GetMonitors();
        if (monitors.Count == 0)
        {
            LblLayout.Text = "No Windows displays were detected.";
            return;
        }

        var previous = cfg.Players.ToDictionary(p => p.Index);
        cfg.Players.Clear();
        if (CbPreset.SelectedIndex <= 0)
        {
            cfg.EnsureDefaultPlayers(count);
            RestoreInputs(cfg.Players, previous);
        }
        else
        {
            var primary = monitors.FirstOrDefault(m => m.Primary, monitors[0]);
            var requested = CbPreset.SelectedIndex == 1 ? SplitMode.TwoStacked
                : CbPreset.SelectedIndex == 2 ? SplitMode.TwoSideBySide
                : SplitMode.FourGrid;
            var split = count == 1 ? SplitMode.Full : count > 2 ? SplitMode.FourGrid : requested;
            for (var i = 0; i < count; i++)
            {
                var old = previous.GetValueOrDefault(i);
                cfg.Players.Add(new PlayerSlot
                {
                    Index = i, MonitorDevice = primary.DeviceName, Split = split, Region = i,
                    Pad = old?.Pad ?? i, Keyboard = old?.Keyboard ?? false
                });
            }
        }
        _state.Save();
        RefreshSummary();
        await _retile();
    }

    private static void RestoreInputs(IEnumerable<PlayerSlot> slots, IReadOnlyDictionary<int, PlayerSlot> previous)
    {
        foreach (var slot in slots)
        {
            if (!previous.TryGetValue(slot.Index, out var old)) continue;
            slot.Pad = old.Pad;
            slot.Keyboard = old.Keyboard;
        }
    }

    private async Task LaunchAsync()
    {
        if (_launching) return;
        SaveSessionChoices();
        SaveOptions();
        var blockers = SetupStatus.Blockers(_state.Config);
        if (blockers.Count > 0)
        {
            LblLaunchHint.Text = "Setup needs attention: " + blockers[0];
            _openSetup();
            return;
        }

        _launching = true;
        BtnLaunch.IsEnabled = false;
        try { await _launch(CbPlayers.SelectedIndex + 1); }
        finally { _launching = false; BtnLaunch.IsEnabled = true; }
    }

    private void RefreshSummary()
    {
        if (_loading) return;
        var cfg = _state.Config;
        var items = SetupStatus.Evaluate(cfg).Where(i => i.Essential).ToList();
        var ready = items.Count(i => i.Ok);
        var blockers = items.Count - ready;
        var percent = items.Count == 0 ? 100 : ready * 100d / items.Count;
        ReadinessProgress.BeginAnimation(ProgressBar.ValueProperty,
            new DoubleAnimation(ReadinessProgress.Value, percent, TimeSpan.FromMilliseconds(420))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        LblPercent.Text = $"{percent:0}%";
        LblReadiness.Text = blockers == 0 ? "Setup passed · ready to launch" : $"{blockers} setup blocker(s) · review Setup";
        LblLaunchHint.Text = blockers == 0
            ? "These choices are saved as you make them. Launch starts every player in parallel."
            : "Configure the session now, then clear the remaining red items on Setup.";
        BtnLaunch.Content = blockers == 0 ? $"Launch {Math.Max(1, cfg.Players.Count)} player{(cfg.Players.Count == 1 ? "" : "s")}" : "Review setup";
        LblModePill.Text = cfg.Mode;
        var pads = Enumerable.Range(0, 4).Count(i => Native.PadConnected((uint)i));
        LblRigPill.Text = $"{Native.GetMonitors().Count} displays · {pads} pads";
        var engine = cfg.SessionEngine == SessionEngine.SingleInstanceExperimental ? "1 instance · experimental" : cfg.Mode;
        LblModePill.Text = cfg.SessionEngine == SessionEngine.SingleInstanceExperimental ? "SINGLE INSTANCE · EXPERIMENTAL" : cfg.Mode;
        LblLaunchSummary.Text = $"{Math.Max(1, cfg.Players.Count)} player(s)\n{engine}\n{cfg.FrameLimit} FPS cap";
        LblControllers.Text = ControllerSummary(cfg);
        LblLayout.Text = LayoutSummary(cfg);
        ChkAutoJoin.IsEnabled = cfg.Mode == "BeamMP";
        var multi = cfg.SessionEngine == SessionEngine.MultiInstance;
        CbMode.IsEnabled = multi;
        CbPlayers.IsEnabled = multi;
        ChkIsolate.IsEnabled = multi;
        ChkProtoInput.IsEnabled = multi;
        CbAudioMix.IsEnabled = multi;
    }

    private static string ControllerSummary(AppConfig cfg)
    {
        if (cfg.Players.Count == 0) return "No players assigned yet.";
        return string.Join("  ·  ", cfg.Players.OrderBy(p => p.Index).Select(p =>
            $"P{p.Index + 1}: {(p.Keyboard ? "keyboard" : p.Pad >= 0 ? $"pad {p.Pad}" : "no input")}"));
    }

    private static string LayoutSummary(AppConfig cfg)
    {
        if (cfg.Players.Count == 0) return "No screen layout saved.";
        var screens = cfg.Players.Select(p => p.MonitorDevice).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var mode = cfg.Players.Select(p => p.Split).Distinct().Count() == 1
            ? cfg.Players[0].Split switch
            {
                SplitMode.TwoStacked => "stacked", SplitMode.TwoSideBySide => "side by side",
                SplitMode.FourGrid => "four-grid", _ => "full displays"
            }
            : "custom";
        return $"{cfg.Players.Count} player(s) across {screens} display(s) · {mode}.";
    }

    private async Task LoadMapsAsync()
    {
        _mapsLoaded = true;
        LblMapStatus.Text = "Reading installed BeamNG maps and previews…";
        var maps = await Task.Run(() => MapCatalog.Discover(_state.Config));
        _activeMap = ServerConfig.Read(_state.Config).GetValueOrDefault("Map", "/levels/gridmap_v2/info.json");
        _selectedMap = _activeMap;
        MapList.Items.Clear();
        _mapCards.Clear();
        foreach (var map in maps) MapList.Items.Add(BuildMapCard(map));
        RefreshMapState();
        _ = Dispatcher.BeginInvoke(UpdateMapScrollButtons, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private UIElement BuildMapCard(BeamMap map)
    {
        var imageHost = new Grid { Height = 86, Background = (Brush)FindResource("BgAlt") };
        imageHost.Children.Add(new TextBlock
        {
            Text = "BEAMNG",
            Foreground = (Brush)FindResource("Faint"),
            FontWeight = FontWeights.Bold,
            FontSize = 10,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
        if (map.Thumbnail is { Length: > 0 })
        {
            using var stream = new MemoryStream(map.Thumbnail, writable: false);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 340;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            imageHost.Children.Add(new Image { Source = bitmap, Stretch = Stretch.UniformToFill });
        }

        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(86) });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.Children.Add(imageHost);
        var title = new TextBlock
        {
            Text = map.Title,
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(9, 7, 9, 8)
        };
        Grid.SetRow(title, 1);
        content.Children.Add(title);

        var card = new Border
        {
            Width = 172,
            Background = (Brush)FindResource("CardHi"),
            BorderBrush = (Brush)FindResource("Line"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            ClipToBounds = true,
            Margin = new Thickness(0, 0, 9, 0),
            Child = content,
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = map.ServerPath
        };
        card.MouseLeftButtonUp += (_, _) => SelectMap(map.ServerPath);
        _mapCards[map.ServerPath] = card;
        return card;
    }

    private void SelectMap(string mapPath)
    {
        _selectedMap = mapPath;
        RefreshMapState();
    }

    private async Task SwitchMapAsync()
    {
        if (string.IsNullOrWhiteSpace(_selectedMap)) return;
        BtnSwitchMap.IsEnabled = false;
        try
        {
            if (await _switchMap(_selectedMap)) _activeMap = _selectedMap;
        }
        finally { RefreshMapState(); }
    }

    private void RefreshMapState()
    {
        var beamMp = _state.Config.Mode == "BeamMP";
        MapCard.Opacity = beamMp ? 1 : 0.55;
        foreach (var pair in _mapCards)
        {
            var selected = pair.Key.Equals(_selectedMap, StringComparison.OrdinalIgnoreCase);
            pair.Value.BorderBrush = (Brush)FindResource(selected ? "Accent" : "Line");
            pair.Value.BorderThickness = new Thickness(selected ? 2 : 1);
        }

        var title = _mapCards.Keys.FirstOrDefault(path => path.Equals(_selectedMap, StringComparison.OrdinalIgnoreCase));
        var changed = !_selectedMap.Equals(_activeMap, StringComparison.OrdinalIgnoreCase);
        var running = ServerConfig.IsRunning();
        BtnSwitchMap.Content = running
            ? "Switch map · keep games open"
            : changed ? "Use for next launch" : "Selected for next launch";
        BtnSwitchMap.IsEnabled = beamMp && !string.IsNullOrWhiteSpace(_selectedMap) && changed;
        LblMapStatus.Text = !beamMp
            ? "Map selection applies to BeamMP shared-world sessions."
            : string.IsNullOrWhiteSpace(title)
                ? $"Current server map: {_activeMap}"
                : changed && running
                    ? _state.Config.AutoJoinBeamMp
                        ? "New map selected. Switch restarts only the local server; both BeamNG instances stay open and reconnect."
                        : "New map selected. Both games stay open, but Auto Join is off, so each player must reconnect after the switch."
                    : running
                        ? "This map is live. Choose another thumbnail to prepare a switch."
                        : "Selected for the next BeamMP launch.";
    }

    private void ScrollMaps(int direction)
    {
        var amount = Math.Max(360, MapScroller.ViewportWidth * 0.72);
        MapScroller.ScrollToHorizontalOffset(Math.Clamp(
            MapScroller.HorizontalOffset + direction * amount, 0, MapScroller.ScrollableWidth));
    }

    private void UpdateMapScrollButtons()
    {
        BtnMapPrevious.IsEnabled = MapScroller.HorizontalOffset > 1;
        BtnMapNext.IsEnabled = MapScroller.HorizontalOffset < MapScroller.ScrollableWidth - 1;
    }
}
