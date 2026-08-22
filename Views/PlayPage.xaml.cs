using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using BeamSplit.Core;

namespace BeamSplit.Views;

public partial class PlayPage : UserControl
{
    private readonly AppState _state = AppState.Current;
    private readonly Func<int, Task> _launch;
    private readonly Func<Task> _retile;
    private readonly Action _openSetup;
    private readonly Action _openScreens;
    private bool _loading;
    private bool _launching;

    public PlayPage(Func<int, Task> launch, Func<Task> retile, Action openSetup, Action openScreens)
    {
        InitializeComponent();
        _launch = launch;
        _retile = retile;
        _openSetup = openSetup;
        _openScreens = openScreens;

        BtnLaunch.Click += async (_, _) => await LaunchAsync();
        BtnOpenSetup.Click += (_, _) => _openSetup();
        BtnOpenScreens.Click += (_, _) => _openScreens();
        BtnConfigureControllers.Click += (_, _) => _openScreens();
        BtnApplyPreset.Click += async (_, _) => await ApplyPresetAsync();
        CbMode.SelectionChanged += (_, _) => SaveSessionChoices();
        CbPlayers.SelectionChanged += (_, _) => SaveSessionChoices();
        CbAudioMix.SelectionChanged += (_, _) => SaveOptions();
        foreach (var check in new[] { ChkBorderless, ChkIsolate, ChkProtoInput, ChkPlayerMods,
                                      ChkCinematic, ChkAutoJoin, ChkAudioBackground })
        {
            check.Checked += (_, _) => SaveOptions();
            check.Unchecked += (_, _) => SaveOptions();
        }
        TxtFrameLimit.LostFocus += (_, _) => SaveOptions();
        Loaded += (_, _) => LoadConfig();
    }

    private void LoadConfig()
    {
        _loading = true;
        var cfg = _state.Config;
        CbMode.SelectedIndex = cfg.Mode == "Solo" ? 1 : 0;
        CbPlayers.SelectedIndex = Math.Clamp(Math.Max(1, cfg.Players.Count) - 1, 0, 3);
        CbPreset.SelectedIndex = 0;
        ChkBorderless.IsChecked = cfg.Borderless;
        ChkIsolate.IsChecked = cfg.Isolate;
        ChkProtoInput.IsChecked = cfg.UseProtoInput;
        ChkPlayerMods.IsChecked = cfg.UsePlayerMods;
        ChkCinematic.IsChecked = cfg.LaunchCinematic;
        ChkAutoJoin.IsChecked = cfg.AutoJoinBeamMp;
        ChkAudioBackground.IsChecked = cfg.AudioInBackground;
        TxtFrameLimit.Text = cfg.FrameLimit.ToString();
        CbAudioMix.SelectedIndex = cfg.AudioMixMode switch { "All" => 1, "P0Only" => 2, _ => 0 };
        _loading = false;
        RefreshSummary();
    }

    private void SaveSessionChoices()
    {
        if (_loading || CbPlayers.SelectedIndex < 0) return;
        var cfg = _state.Config;
        cfg.Mode = CbMode.SelectedIndex == 1 ? "Solo" : "BeamMP";
        cfg.EnsureDefaultPlayers(CbPlayers.SelectedIndex + 1);
        _state.Save();
        RefreshSummary();
    }

    private void SaveOptions()
    {
        if (_loading) return;
        var cfg = _state.Config;
        cfg.Borderless = ChkBorderless.IsChecked == true;
        cfg.Isolate = ChkIsolate.IsChecked == true;
        cfg.UseProtoInput = ChkProtoInput.IsChecked == true;
        cfg.UsePlayerMods = ChkPlayerMods.IsChecked == true;
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
        LblLaunchSummary.Text = $"{Math.Max(1, cfg.Players.Count)} player(s)\n{cfg.Mode}\n{cfg.FrameLimit} FPS cap";
        LblControllers.Text = ControllerSummary(cfg);
        LblLayout.Text = LayoutSummary(cfg);
        ChkAutoJoin.IsEnabled = cfg.Mode == "BeamMP";
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
}
