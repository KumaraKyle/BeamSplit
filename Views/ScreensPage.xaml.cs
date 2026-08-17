using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using BeamSplit.Core;
using Microsoft.Win32;

namespace BeamSplit.Views;

/// <summary>
/// Visual player assignment: a to-scale map of the real displays, each splittable into
/// regions, with controllers dragged onto them.
///
/// Everything is dynamic - monitor count, player count and device count all come from
/// the machine at runtime. Nothing assumes two of anything, and displays are keyed by
/// DeviceName rather than index because enumeration order is not stable.
/// </summary>
public partial class ScreensPage : UserControl
{
    private const string PadFormat = "BeamSplit.Pad";

    private readonly AppState _state = AppState.Current;
    private readonly Func<int, Task> _launch;
    private readonly Func<Task> _retile;
    private List<MonitorInfo> _monitors = [];

    public ScreensPage(Func<int, Task> launch, Func<Task> retile)
    {
        InitializeComponent();
        _launch = launch;
        _retile = retile;

        BtnRescan.Click += (_, _) => Rebuild();
        BtnIdentify.Click += async (_, _) => await IdentifyAsync();
        BtnClear.Click += (_, _) => { _state.Config.Players.Clear(); _state.Save(); Rebuild(); };
        BtnApply.Click += async (_, _) => await ApplyLiveAsync();
        BtnLaunch.Click += async (_, _) =>
        {
            Commit();
            await _launch(Math.Max(1, _state.Config.Players.Count));
        };

        // re-render when displays are plugged, unplugged or rearranged
        SystemEvents.DisplaySettingsChanged += OnDisplaysChanged;
        Unloaded += (_, _) => SystemEvents.DisplaySettingsChanged -= OnDisplaysChanged;

        SizeChanged += (_, _) => LayoutMap();
        Loaded += (_, _) => Rebuild();
    }

    private void OnDisplaysChanged(object? s, EventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        _state.Log("Display layout changed - re-reading monitors.");
        Rebuild();
    });

    // ------------------------------------------------------------------ tray
    private void Rebuild()
    {
        _monitors = Native.GetMonitors();
        BuildTray();
        LayoutMap();
        UpdateSummary();
    }

    /// <summary>Pad index used for the keyboard/mouse chip. Negative = "no controller".</summary>
    private const int KeyboardPad = -1;

    private void BuildTray()
    {
        Tray.Children.Clear();
        var pads = 0;

        for (uint i = 0; i < 4; i++)
        {
            var connected = Native.PadConnected(i);
            if (!connected && !UsedPads().Contains((int)i)) continue;
            pads++;
            Tray.Children.Add(MakeChip((int)i, connected, draggable: true));
        }

        // Keyboard and mouse is always available as a player - it needs no detection,
        // and its instance simply gets no controller at all.
        Tray.Children.Add(MakeChip(KeyboardPad, connected: true, draggable: true));

        if (pads == 0)
        {
            Tray.Children.Add(new TextBlock
            {
                Text = "No controllers detected - wake them and press Rescan.",
                Foreground = (Brush)FindResource("Muted"),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0)
            });
        }
    }

    private HashSet<int> UsedPads() => _state.Config.Players.Where(p => p.Pad >= 0 && !p.Keyboard).Select(p => p.Pad).ToHashSet();

    private Border MakeChip(int pad, bool connected, bool draggable)
    {
        var used = UsedPads().Contains(pad);

        var chip = new Border
        {
            Background = (Brush)FindResource(used ? "CardHi" : "Card"),
            BorderBrush = (Brush)FindResource(connected ? "Accent" : "Line"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 0, 8, 0),
            Cursor = Cursors.Hand,
            Tag = pad,
            Opacity = connected ? 1 : 0.5
        };

        var isKeyboard = pad == KeyboardPad;
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock
        {
            Text = isKeyboard ? "\uE765" : "\uE7FC",   // keyboard : gamepad
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 15,
            Foreground = (Brush)FindResource(connected ? "Accent" : "Faint"),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        row.Children.Add(new TextBlock
        {
            Text = isKeyboard ? "Keyboard & mouse" : $"Pad {pad}" + (connected ? "" : "  (off)"),
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center
        });
        chip.Child = row;
        if (isKeyboard)
            chip.ToolTip = "Only the FOCUSED window receives keyboard and mouse input, so this player must have focus - which merges the pads. Best used as the only player, or with the focus guard off.";

        if (draggable)
        {
            chip.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ClickCount != 1) return;
                DragDrop.DoDragDrop(chip, new DataObject(PadFormat, pad), DragDropEffects.Move);
            };
        }
        return chip;
    }

    private async Task IdentifyAsync()
    {
        LblTrayHint.Text = "Press a button on a controller...";
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            for (uint i = 0; i < 4; i++)
            {
                if (!Native.PadAnyInput(i)) continue;
                LblTrayHint.Text = $"That is Pad {i}.";
                FlashChip((int)i);
                return;
            }
            await Task.Delay(60);
        }
        LblTrayHint.Text = "Nothing detected - is the pad awake?";
    }

    private void FlashChip(int pad)
    {
        foreach (var child in Tray.Children.OfType<Border>())
        {
            if ((int?)child.Tag != pad) continue;
            var anim = new DoubleAnimation(1, 0.25, TimeSpan.FromMilliseconds(180))
            {
                AutoReverse = true,
                RepeatBehavior = new RepeatBehavior(4)
            };
            child.BeginAnimation(OpacityProperty, anim);
        }
    }

    // ------------------------------------------------------------------- map
    /// <summary>
    /// Lays displays out left-to-right in the same natural order as their Windows
    /// labels (DISPLAY1, DISPLAY2, DISPLAY3). Desktop coordinates can legitimately be
    /// 2,3,1 when the primary monitor or cables change, which made this assignment page
    /// appear shuffled even though tiling still targeted the correct DeviceName.
    /// </summary>
    private void LayoutMap()
    {
        Map.Children.Clear();
        if (_monitors.Count == 0)
        {
            LblNoMonitors.Visibility = Visibility.Visible;
            return;
        }
        LblNoMonitors.Visibility = Visibility.Collapsed;

        var availW = Math.Max(200, Map.ActualWidth > 0 ? Map.ActualWidth : 700);
        var availH = Math.Max(160, Map.ActualHeight > 0 ? Map.ActualHeight : 300);
        var ordered = _monitors.OrderBy(DisplayOrder).ThenBy(m => m.DeviceName).ToList();
        const double gap = 8;
        var naturalW = Math.Max(1, ordered.Sum(m => m.Width));
        var naturalH = Math.Max(1, ordered.Max(m => m.Height));
        var usableW = Math.Max(1, availW - gap * (ordered.Count - 1));
        var scale = Math.Min(usableW / naturalW, availH / naturalH) * 0.94;
        var cards = ordered.Select(m => (Monitor: m,
            Width: Math.Max(120, m.Width * scale),
            Height: Math.Max(80, m.Height * scale))).ToList();
        var renderedW = cards.Sum(c => c.Width) + gap * (cards.Count - 1);
        var x = (availW - renderedW) / 2;

        foreach (var item in cards)
        {
            var card = BuildMonitorCard(item.Monitor, item.Width, item.Height);
            Canvas.SetLeft(card, x);
            Canvas.SetTop(card, (availH - item.Height) / 2);
            Map.Children.Add(card);
            x += item.Width + gap;
        }
    }

    private static int DisplayOrder(MonitorInfo monitor)
    {
        var name = monitor.DeviceName;
        var marker = name.LastIndexOf("DISPLAY", StringComparison.OrdinalIgnoreCase);
        return marker >= 0 && int.TryParse(name[(marker + 7)..], out var number)
            ? number
            : int.MaxValue;
    }

    private UIElement BuildMonitorCard(MonitorInfo mon, double w, double h)
    {
        var split = SplitFor(mon);

        var outer = new Border
        {
            Width = w,
            Height = h,
            Background = (Brush)FindResource("BgAlt"),
            BorderBrush = (Brush)FindResource(mon.Primary ? "Accent" : "Line"),
            BorderThickness = new Thickness(mon.Primary ? 1.5 : 1),
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(3)
        };

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // header: name + split selector
        var head = new Grid { Margin = new Thickness(8, 6, 6, 4) };
        head.Children.Add(new TextBlock
        {
            Text = $"{mon.DeviceName.Replace(@"\\.\", "")}   {mon.Width}x{mon.Height}{(mon.Primary ? "   primary" : "")}",
            FontSize = 10.5,
            Foreground = (Brush)FindResource("Muted")
        });

        var splitBar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var (mode, glyph, tip) in new[]
                 {
                     (SplitMode.Full,          "\u25A1", "one player"),
                     (SplitMode.TwoStacked,    "\u2338", "two, stacked"),
                     (SplitMode.TwoSideBySide, "\u2337", "two, side by side"),
                     (SplitMode.FourGrid,      "\u229E", "four")
                 })
        {
            var b = new Button
            {
                Content = glyph,
                ToolTip = tip,
                FontSize = 11,
                Padding = new Thickness(5, 1, 5, 1),
                Margin = new Thickness(2, 0, 0, 0),
                Background = mode == split ? (Brush)FindResource("Accent") : Brushes.Transparent,
                Foreground = mode == split ? Brushes.Black : (Brush)FindResource("Muted"),
                Style = (Style)FindResource("Small")
            };
            var captured = mode;
            b.Click += async (_, _) => await SetSplitAsync(mon, captured);
            splitBar.Children.Add(b);
        }
        head.Children.Add(splitBar);
        Grid.SetRow(head, 0);
        root.Children.Add(head);

        // regions
        var regions = new UniformGrid
        {
            Margin = new Thickness(6, 0, 6, 6),
            Rows = split switch
            {
                SplitMode.TwoStacked => 2,
                SplitMode.FourGrid => 2,
                _ => 1
            },
            Columns = split switch
            {
                SplitMode.TwoSideBySide => 2,
                SplitMode.FourGrid => 2,
                _ => 1
            }
        };
        for (var r = 0; r < Tiling.Capacity(split); r++) regions.Children.Add(BuildRegion(mon, split, r));
        Grid.SetRow(regions, 1);
        root.Children.Add(regions);

        outer.Child = root;
        return outer;
    }

    private UIElement BuildRegion(MonitorInfo mon, SplitMode split, int region)
    {
        var slot = _state.Config.Players.FirstOrDefault(p => p.MonitorDevice == mon.DeviceName && p.Region == region && p.Split == split);

        var cell = new Border
        {
            Background = (Brush)FindResource(slot is null ? "Card" : "CardHi"),
            BorderBrush = (Brush)FindResource(slot is null ? "Line" : "Accent"),
            BorderThickness = new Thickness(slot is null ? 1 : 1.5),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(2),
            AllowDrop = true
        };

        var content = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        if (slot is null)
        {
            content.Children.Add(new TextBlock
            {
                Text = "drop a pad",
                FontSize = 10.5,
                Foreground = (Brush)FindResource("Faint")
            });
        }
        else
        {
            var playerNo = _state.Config.Players.IndexOf(slot) + 1;
            content.Children.Add(new TextBlock
            {
                Text = $"Player {playerNo}",
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center
            });
            content.Children.Add(new TextBlock
            {
                Text = slot.Keyboard ? "keyboard & mouse" : slot.Pad >= 0 ? $"pad {slot.Pad}" : "no pad",
                FontSize = 10.5,
                Foreground = (Brush)FindResource("Muted"),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 2, 0, 0)
            });

            var remove = new Button
            {
                Content = "remove",
                Style = (Style)FindResource("Small"),
                FontSize = 10,
                Padding = new Thickness(6, 1, 6, 1),
                Margin = new Thickness(0, 5, 0, 0),
                Background = Brushes.Transparent,
                Foreground = (Brush)FindResource("Faint")
            };
            remove.Click += async (_, _) =>
            {
                InputSetup.SetPad(_state.Config, slot.Index, -1);
                _state.Config.Players.Remove(slot);
                Reindex();
                _state.Save();
                Rebuild();
                await _retile();
            };
            content.Children.Add(remove);

            // allow dragging an assignment to another region
            cell.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ClickCount != 1 || slot.Pad < 0) return;
                DragDrop.DoDragDrop(cell, new DataObject(PadFormat, slot.Pad), DragDropEffects.Move);
            };
        }
        cell.Child = content;

        cell.DragEnter += (_, e) =>
        {
            if (!e.Data.GetDataPresent(PadFormat)) return;
            cell.BorderBrush = (Brush)FindResource("Good");
            cell.BorderThickness = new Thickness(2);
        };
        cell.DragLeave += (_, _) =>
        {
            cell.BorderBrush = (Brush)FindResource(slot is null ? "Line" : "Accent");
            cell.BorderThickness = new Thickness(slot is null ? 1 : 1.5);
        };
        cell.DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(PadFormat) ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        };
        cell.Drop += async (_, e) =>
        {
            if (!e.Data.GetDataPresent(PadFormat)) return;
            var pad = (int)e.Data.GetData(PadFormat)!;
            e.Handled = true;
            await AssignAsync(mon, split, region, pad);
        };

        return cell;
    }

    // ------------------------------------------------------------ assignment
    private SplitMode SplitFor(MonitorInfo mon) =>
        _state.Config.Players.FirstOrDefault(p => p.MonitorDevice == mon.DeviceName)?.Split ?? SplitMode.Full;

    private async Task SetSplitAsync(MonitorInfo mon, SplitMode mode)
    {
        var players = _state.Config.Players;
        var onThis = players.Where(p => p.MonitorDevice == mon.DeviceName).ToList();

        // drop anything that no longer has a region to live in
        foreach (var p in onThis)
        {
            p.Split = mode;
            if (p.Region < Tiling.Capacity(mode)) continue;
            InputSetup.SetPad(_state.Config, p.Index, -1);
            players.Remove(p);
        }
        Reindex();
        _state.Save();
        Rebuild();
        await _retile();
    }

    private async Task AssignAsync(MonitorInfo mon, SplitMode split, int region, int pad)
    {
        var players = _state.Config.Players;
        var keyboard = pad == KeyboardPad;
        var target = players.FirstOrDefault(p =>
            p.MonitorDevice == mon.DeviceName && p.Region == region && p.Split == split);

        // a device can only drive one instance
        var displaced = players.Where(p => p != target && p.Pad == pad && p.Keyboard == keyboard).ToList();
        if (keyboard) displaced.AddRange(players.Where(p => p != target && p.Keyboard && !displaced.Contains(p)));
        foreach (var old in displaced)
        {
            InputSetup.SetPad(_state.Config, old.Index, -1);
            players.Remove(old);
        }

        if (target != null) { target.Pad = pad; target.Keyboard = keyboard; }
        else
        {
            target = new PlayerSlot
            {
                Index = HasRunningSession() ? NextFreeInstance(players) : players.Count,
                MonitorDevice = mon.DeviceName,
                Split = split,
                Region = region,
                Pad = pad,
                Keyboard = keyboard
            };
            players.Add(target);
        }

        Reindex();
        _state.Save();
        Rebuild();
        var live = InputSetup.SetPad(_state.Config, target.Index, pad);
        _state.Log($"P{target.Index}: {(keyboard ? "keyboard & mouse" : $"pad {pad}")} on {mon.DeviceName.Replace(@"\\.\", "")}" +
                   (live ? " (applied live)" : " (saved; relaunch needed for this older session)"));
        await _retile();
    }

    /// <summary>
    /// Instance numbering follows the same DISPLAY1, DISPLAY2, DISPLAY3 order shown by
    /// the page. Regions within one display retain their visual order.
    /// </summary>
    private void Reindex()
    {
        var monitorOrder = _monitors
            .OrderBy(DisplayOrder).ThenBy(m => m.DeviceName)
            .Select((m, i) => (m.DeviceName, i))
            .ToDictionary(x => x.DeviceName, x => x.i);

        var sorted = _state.Config.Players
            .OrderBy(p => monitorOrder.GetValueOrDefault(p.MonitorDevice, 99))
            .ThenBy(p => p.Region)
            .ToList();

        // While games are alive, Index is the identity of the instance folder,
        // process and Proto Input handle. Compacting P1 to P0 when P0 is removed
        // severs all three live mappings. Only normalize indexes while no session is
        // running; LaunchAsync also creates a fresh contiguous assignment next time.
        if (!HasRunningSession())
            for (var i = 0; i < sorted.Count; i++) sorted[i].Index = i;
        _state.Config.Players = sorted;
    }

    private static bool HasRunningSession() => Tiling.GameWindows().Count > 0;

    private static int NextFreeInstance(IEnumerable<PlayerSlot> players)
    {
        var used = players.Select(p => p.Index).ToHashSet();
        var index = 0;
        while (used.Contains(index)) index++;
        return index;
    }

    private void Commit()
    {
        Reindex();
        _state.Save();
    }

    /// <summary>
    /// Push the current pad assignment to a running session. The proxy re-reads its ini
    /// about once a second, so this takes effect without relaunching.
    /// </summary>
    private async Task ApplyLiveAsync()
    {
        Commit();
        var n = 0;
        var saved = 0;
        foreach (var p in _state.Config.Players)
        {
            if (!Instances.Exists(_state.Config, p.Index)) continue;
            if (InputSetup.SetPad(_state.Config, p.Index, p.Pad)) n++;
            else saved++;
        }
        _state.Log(saved == 0
            ? $"Applied pad assignment to {n} running instance(s) immediately."
            : $"Applied {n} live; saved {saved} for the next launch (no active injection handle)." );
        await _retile();
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        var players = _state.Config.Players.Count;
        var screens = _state.Config.Players.Select(p => p.MonitorDevice).Distinct().Count();
        LblSummary.Text = players == 0
            ? $"{_monitors.Count} display(s) detected - no players assigned yet"
            : $"{players} player(s) across {screens} screen(s)";
    }
}
