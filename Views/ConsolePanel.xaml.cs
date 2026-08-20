using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using BeamSplit.Core;

namespace BeamSplit.Views;

/// <summary>
/// Aggregated live log of the app, the BeamMP server and every instance, so nothing
/// needs an external tail. Sources appear and disappear with instances.
/// </summary>
public partial class ConsolePanel : UserControl
{
    private readonly AppState _state = AppState.Current;
    private readonly HashSet<string> _muted = [];
    private readonly List<LogLine> _buffer = [];
    private Regex? _filter;

    /// <summary>Commands typed in the bar are handled by MainWindow, which owns the Launcher.</summary>
    public Func<string, Task>? CommandHandler { get; set; }

    public ConsolePanel()
    {
        InitializeComponent();

        _state.Logged += OnLogged;
        Unloaded += (_, _) => _state.Logged -= OnLogged;

        BtnClear.Click += (_, _) => { _buffer.Clear(); Lines.Items.Clear(); };
        BtnCopy.Click += (_, _) => CopyDiagnostics();
        BtnBundle.Click += (_, _) => CreateSupportBundle();
        TxtSearch.TextChanged += (_, _) => ApplyFilter();

        TxtCmd.TextChanged += (_, _) => Hint.Visibility = TxtCmd.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        TxtCmd.KeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter || TxtCmd.Text.Trim().Length == 0) return;
            var cmd = TxtCmd.Text.Trim();
            TxtCmd.Clear();
            _state.Log($"> {cmd}");
            if (CommandHandler != null) await CommandHandler(cmd);
        };

        foreach (var l in _state.Snapshot().TakeLast(400)) Add(l, scroll: false);
        ScrollIfLocked();
    }

    // Launchers can emit bursts of output. Normal-priority callbacks run ahead of WPF's
    // render pass and used to visibly pin the launch film even after the launcher itself
    // moved off-thread. Console rendering is diagnostic, so keep it below animation/input.
    private void OnLogged(LogLine line) =>
        Dispatcher.BeginInvoke(() => Add(line), DispatcherPriority.Background);

    private void Add(LogLine line, bool scroll = true)
    {
        _buffer.Add(line);
        if (_buffer.Count > 4000) _buffer.RemoveRange(0, 800);

        EnsureChip(line.Tag);
        if (!Visible(line)) return;

        Lines.Items.Add(Render(line));
        if (Lines.Items.Count > 2000) Lines.Items.RemoveAt(0);
        if (scroll) ScrollIfLocked();
    }

    private bool Visible(LogLine l) =>
        !_muted.Contains(l.Tag) && (_filter is null || _filter.IsMatch(l.Text) || _filter.IsMatch(l.Tag));

    private UIElement Render(LogLine l)
    {
        var brushKey = l.Text.Contains("ERROR", StringComparison.OrdinalIgnoreCase) ||
                       l.Text.Contains("FAILED", StringComparison.OrdinalIgnoreCase) ||
                       l.Text.Contains("Exception", StringComparison.OrdinalIgnoreCase) ? "Bad"
                     : l.Text.Contains("WARN", StringComparison.OrdinalIgnoreCase) ||
                       l.Text.Contains("missing", StringComparison.OrdinalIgnoreCase) ? "Warn"
                     : l.Text.Contains("synced", StringComparison.OrdinalIgnoreCase) ||
                       l.Text.Contains("Connected", StringComparison.OrdinalIgnoreCase) ? "Good"
                     : "Muted";

        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new TextBlock
        {
            Text = l.When.ToString("HH:mm:ss"),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11.5,
            Foreground = (Brush)FindResource("Faint"),
            Margin = new Thickness(0, 0, 10, 0)
        });
        panel.Children.Add(new TextBlock
        {
            Text = l.Tag,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11.5,
            Width = 74,
            Foreground = (Brush)FindResource("Accent"),
            Opacity = 0.75,
            Margin = new Thickness(0, 0, 10, 0)
        });
        panel.Children.Add(new TextBlock
        {
            Text = l.Text,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11.5,
            Foreground = (Brush)FindResource(brushKey),
            TextWrapping = TextWrapping.NoWrap
        });
        return panel;
    }

    private void EnsureChip(string tag)
    {
        foreach (ToggleButton existing in Chips.Children.OfType<ToggleButton>())
            if ((string)existing.Tag == tag) return;

        var chip = new ToggleButton
        {
            Content = tag,
            Tag = tag,
            IsChecked = true,
            Margin = new Thickness(0, 0, 6, 0),
            Padding = new Thickness(9, 4, 9, 4),
            FontSize = 11.5,
            Background = Brushes.Transparent,
            Foreground = (Brush)FindResource("Muted"),
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand
        };
        chip.Checked += (_, _) => { _muted.Remove(tag); Rebuild(); };
        chip.Unchecked += (_, _) => { _muted.Add(tag); Rebuild(); };
        Chips.Children.Add(chip);
    }

    private void ApplyFilter()
    {
        var text = TxtSearch.Text.Trim();
        if (text.Length == 0) _filter = null;
        else
        {
            try { _filter = new Regex(text, RegexOptions.IgnoreCase); }
            catch { _filter = new Regex(Regex.Escape(text), RegexOptions.IgnoreCase); }
        }
        Rebuild();
    }

    private void Rebuild()
    {
        Lines.Items.Clear();
        foreach (var l in _buffer.Where(Visible).TakeLast(2000)) Lines.Items.Add(Render(l));
        ScrollIfLocked();
    }

    private void ScrollIfLocked()
    {
        if (BtnLock.IsChecked == true) Scroll.ScrollToEnd();
    }

    /// <summary>Config plus recent lines from every source - for pasting when asking for help.</summary>
    private void CopyDiagnostics()
    {
        var cfg = _state.Config;
        var sb = new StringBuilder();
        sb.AppendLine("BeamSplit diagnostics");
        sb.AppendLine($"time        : {DateTime.Now}");
        sb.AppendLine($"game        : {cfg.GameRoot}  (v{Detect.GameVersion(cfg)})");
        sb.AppendLine($"instances   : {cfg.InstancesDir}");
        sb.AppendLine($"mode        : {cfg.Mode}   players {cfg.Players.Count}   basePort {cfg.BasePort}");
        sb.AppendLine($"modZip      : {cfg.ModZip}");
        sb.AppendLine($"isolate     : {cfg.Isolate}   watchdog {cfg.Watchdog}   borderless {cfg.Borderless}");
        foreach (var m in Native.GetMonitors())
            sb.AppendLine($"monitor     : {m.DeviceName} {m.Width}x{m.Height} @ {m.X},{m.Y}{(m.Primary ? " primary" : "")}");
        for (var i = 0; i < cfg.Players.Count; i++)
        {
            var missing = Instances.Exists(cfg, i) ? InputSetup.Verify(cfg, i) : ["(not built)"];
            sb.AppendLine($"P{i}          : pad {cfg.Players[i].Pad}  {(missing.Count == 0 ? "ok" : "MISSING " + string.Join(",", missing))}");
        }
        sb.AppendLine();
        sb.AppendLine("--- recent log ---");
        foreach (var l in _buffer.TakeLast(300)) sb.AppendLine(l.ToString());

        try { Clipboard.SetText(SupportBundle.Redact(sb.ToString(), cfg)); _state.Log("Redacted diagnostics copied to the clipboard."); }
        catch (Exception ex) { _state.Log($"Clipboard failed: {ex.Message}"); }
    }

    private void CreateSupportBundle()
    {
        try
        {
            var path = SupportBundle.Create(_state.Config, _buffer);
            Clipboard.SetText(path);
            _state.Log($"Support bundle created; path copied to clipboard: {path}");
        }
        catch (Exception ex) { _state.Log($"Support bundle failed: {ex.Message}"); }
    }
}
