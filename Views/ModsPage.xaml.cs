using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BeamSplit.Core;

namespace BeamSplit.Views;

public partial class ModsPage : UserControl
{
    private readonly AppState _state = AppState.Current;
    private readonly List<ModRow> _rows = [];

    public ModsPage()
    {
        InitializeComponent();
        BtnBrowse.Click += (_, _) => Browse();
        BtnDetect.Click += (_, _) => UseDefault();
        BtnScan.Click += (_, _) => Scan();
        BtnAllPlayers.Click += (_, _) => SetChecks(players: true, value: true);
        BtnNoPlayers.Click += (_, _) => SetChecks(players: true, value: false);
        BtnAllServer.Click += (_, _) => SetChecks(players: false, value: true);
        BtnNoServer.Click += (_, _) => SetChecks(players: false, value: false);
        BtnApply.Click += async (_, _) => await ApplyAsync();
        Loaded += (_, _) => LoadConfig();
    }

    private void LoadConfig()
    {
        var cfg = _state.Config;
        cfg.ModsSourceDir ??= ModManager.DetectDefaultSource();
        TxtSource.Text = cfg.ModsSourceDir ?? "";
        ChkPlayers.IsChecked = cfg.UsePlayerMods;
        Scan();
    }

    private void UseDefault()
    {
        var found = ModManager.DetectDefaultSource();
        if (found == null)
        {
            LblStatus.Text = "BeamNG's default mods folder was not found yet. Open BeamNG once or choose it manually.";
            return;
        }
        TxtSource.Text = found;
        Scan();
    }

    private void Browse()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose the BeamNG mods folder",
            Multiselect = false
        };
        if (Directory.Exists(TxtSource.Text)) dlg.InitialDirectory = TxtSource.Text;
        if (dlg.ShowDialog() != true) return;
        TxtSource.Text = dlg.FolderName;
        Scan();
    }

    private void Scan()
    {
        var packages = ModManager.Discover(TxtSource.Text.Trim());
        var cfg = _state.Config;
        var playerSet = cfg.PlayerModFiles.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var serverSet = cfg.ServerModFiles.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var firstScan = !cfg.ModsConfigured;

        _rows.Clear();
        ModList.Items.Clear();
        foreach (var package in packages)
        {
            var player = new CheckBox
            {
                IsChecked = firstScan || playerSet.Contains(package.RelativePath),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0)
            };
            var server = new CheckBox
            {
                IsChecked = serverSet.Contains(package.RelativePath),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0)
            };
            var grid = new Grid { MinHeight = 43 };
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(105) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(105) });
            var name = new TextBlock
            {
                Text = package.RelativePath,
                ToolTip = package.FullPath,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(13, 0, 8, 0)
            };
            var size = new TextBlock
            {
                Text = Size(package.Bytes),
                Foreground = (Brush)FindResource("Muted"),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 0, 10, 0)
            };
            var packageCell = new Grid();
            packageCell.Children.Add(name);
            packageCell.Children.Add(size);
            Grid.SetColumn(player, 1);
            Grid.SetColumn(server, 2);
            grid.Children.Add(packageCell);
            grid.Children.Add(player);
            grid.Children.Add(server);
            var border = new Border
            {
                BorderBrush = (Brush)FindResource("Line"),
                BorderThickness = new Thickness(0, ModList.Items.Count == 0 ? 0 : 1, 0, 0),
                Child = grid
            };
            ModList.Items.Add(border);
            _rows.Add(new ModRow(package, player, server));
        }

        LblEmpty.Visibility = packages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        LblEmpty.Text = Directory.Exists(TxtSource.Text.Trim())
            ? "No ZIP mods found. BeamSplit ignores multiplayer downloads so it cannot re-share BeamMP.zip."
            : "That source folder does not exist. Choose your BeamNG user folder's mods directory.";
        var total = packages.Sum(p => p.Bytes);
        LblStatus.Text = packages.Count == 0
            ? "Nothing to apply yet."
            : $"Found {packages.Count} package(s), {Size(total)} total. Tick where each package should go.";
    }

    private async Task ApplyAsync()
    {
        var source = TxtSource.Text.Trim();
        if (!Directory.Exists(source))
        {
            LblStatus.Text = "Choose a valid mods folder first.";
            return;
        }

        var cfg = _state.Config;
        cfg.ModsSourceDir = source;
        cfg.ModsConfigured = true;
        cfg.UsePlayerMods = ChkPlayers.IsChecked == true;
        cfg.PlayerModFiles = _rows.Where(r => r.Player.IsChecked == true).Select(r => r.Package.RelativePath).ToList();
        cfg.ServerModFiles = _rows.Where(r => r.Server.IsChecked == true).Select(r => r.Package.RelativePath).ToList();
        _state.Save();

        BtnApply.IsEnabled = false;
        LblStatus.Text = "Synchronising selected packages...";
        try
        {
            var playerCount = Math.Max(Instances.CountBuilt(cfg), cfg.Players.Count);
            await Task.Run(() => ModManager.Apply(cfg, playerCount, _state.Progress()));
            _state.Save();
            LblStatus.Text = $"Applied: {cfg.UsePlayerMods switch { true => cfg.PlayerModFiles.Count, false => 0 }} personal package(s) across {playerCount} profile(s), " +
                             $"and {cfg.ManagedServerModFiles.Count} server package(s)." +
                             (ServerConfig.IsRunning() ? " Restart the server to publish its new list." : "");
        }
        catch (Exception ex)
        {
            LblStatus.Text = "Mod sync stopped: " + ex.Message;
            _state.Log("Mod sync failed: " + ex.Message);
        }
        finally { BtnApply.IsEnabled = true; }
    }

    private void SetChecks(bool players, bool value)
    {
        foreach (var row in _rows)
            if (players) row.Player.IsChecked = value;
            else row.Server.IsChecked = value;
    }

    private static string Size(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024d * 1024 * 1024):0.0} GB",
        >= 1024L * 1024 => $"{bytes / (1024d * 1024):0.0} MB",
        >= 1024L => $"{bytes / 1024d:0} KB",
        _ => $"{bytes} B"
    };

    private sealed record ModRow(ModPackage Package, CheckBox Player, CheckBox Server);
}
