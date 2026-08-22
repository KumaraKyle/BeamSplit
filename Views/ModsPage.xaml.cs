using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BeamSplit.Core;

namespace BeamSplit.Views;

public partial class ModsPage : UserControl
{
    private readonly AppState _state = AppState.Current;
    private readonly List<ModRow> _rows = [];
    private IReadOnlyList<OfficialMod> _repoMods = [];
    private int _repoPage = 1;
    private bool _repoBusy;

    public ModsPage()
    {
        InitializeComponent();
        BtnBrowse.Click += (_, _) => Browse();
        BtnDetect.Click += (_, _) => UseDefault();
        BtnScan.Click += (_, _) => Scan();
        BtnAllServer.Click += (_, _) => SetServerChecks(true);
        BtnNoServer.Click += (_, _) => SetServerChecks(false);
        BtnApply.Click += async (_, _) => await ApplyAsync();
        BtnRepoRefresh.Click += async (_, _) => await LoadRepositoryAsync();
        BtnRepoPrev.Click += async (_, _) => { if (_repoPage > 1) { _repoPage--; await LoadRepositoryAsync(); } };
        BtnRepoNext.Click += async (_, _) => { _repoPage++; await LoadRepositoryAsync(); };
        BtnRepoOpenSite.Click += (_, _) => OpenOfficial(OfficialModRepository.BaseUri + "resources/");
        BtnRepoFolder.Click += (_, _) =>
        {
            Directory.CreateDirectory(ModManager.RepositorySource);
            Process.Start("explorer.exe", ModManager.RepositorySource);
        };
        TxtRepoSearch.TextChanged += (_, _) => RenderRepository();
        CbRepoOrder.SelectionChanged += async (_, _) =>
        {
            if (!IsLoaded) return;
            _repoPage = 1;
            await LoadRepositoryAsync();
        };
        RepoExpander.Expanded += async (_, _) =>
        {
            if (_repoMods.Count == 0) await LoadRepositoryAsync();
        };
        Loaded += (_, _) => LoadConfig();
    }

    private void LoadConfig()
    {
        var cfg = _state.Config;
        cfg.ModsSourceDir ??= ModManager.DetectDefaultSource();
        try
        {
            if (!string.IsNullOrWhiteSpace(cfg.ModsSourceDir))
                cfg.ModsSourceDir = ModManager.ResolvePlayerSource(cfg.ModsSourceDir);
        }
        catch { }
        TxtSource.Text = cfg.ModsSourceDir ?? "";
        ChkPlayers.IsChecked = cfg.UsePlayerMods;
        ChkRepo.IsChecked = cfg.UseRepositoryMods;
        Scan();
    }

    private async Task LoadRepositoryAsync()
    {
        if (_repoBusy) return;
        _repoBusy = true;
        RepoProgress.Visibility = Visibility.Visible;
        RepoProgress.IsIndeterminate = true;
        BtnRepoRefresh.IsEnabled = BtnRepoPrev.IsEnabled = BtnRepoNext.IsEnabled = false;
        LblRepoStatus.Text = $"Loading official repository page {_repoPage}…";
        try
        {
            var order = CbRepoOrder.SelectedIndex switch
            {
                1 => "download_count",
                2 => "rating_weighted",
                3 => "title",
                _ => ""
            };
            _repoMods = await OfficialModRepository.BrowseAsync(order, _repoPage);
            RenderRepository();
        }
        catch (Exception ex)
        {
            RepoList.Items.Clear();
            LblRepoStatus.Text = "Could not load beamng.com: " + ex.Message;
        }
        finally
        {
            _repoBusy = false;
            RepoProgress.Visibility = Visibility.Collapsed;
            BtnRepoRefresh.IsEnabled = BtnRepoNext.IsEnabled = true;
            BtnRepoPrev.IsEnabled = _repoPage > 1;
        }
    }

    private void RenderRepository()
    {
        var query = TxtRepoSearch.Text.Trim();
        var visible = string.IsNullOrWhiteSpace(query)
            ? _repoMods
            : _repoMods.Where(mod =>
                mod.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                mod.Author.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                mod.Category.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                mod.Tagline.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();

        RepoList.Items.Clear();
        foreach (var mod in visible) RepoList.Items.Add(BuildRepositoryRow(mod));
        LblRepoStatus.Text = _repoMods.Count == 0
            ? "No resources were returned. The official repository may be temporarily unavailable."
            : $"Official page {_repoPage} · showing {visible.Count} of {_repoMods.Count} resources" +
              (string.IsNullOrWhiteSpace(query) ? "" : " matching this filter");
        BtnRepoPrev.IsEnabled = !_repoBusy && _repoPage > 1;
        BtnRepoNext.IsEnabled = !_repoBusy && _repoMods.Count > 0;
    }

    private UIElement BuildRepositoryRow(OfficialMod mod)
    {
        var grid = new Grid { MinHeight = 90, Margin = new Thickness(13, 9, 10, 9) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var thumbnail = BuildRepositoryThumbnail(mod);
        grid.Children.Add(thumbnail);

        var copy = new StackPanel();
        copy.Children.Add(new TextBlock
        {
            Text = mod.Title + (string.IsNullOrWhiteSpace(mod.Version) ? "" : "  " + mod.Version),
            FontSize = 13.5, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis
        });
        copy.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(mod.Tagline) ? $"{mod.Author} · {mod.Category}" : mod.Tagline,
            Foreground = (Brush)FindResource("Muted"), FontSize = 11.5,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 3, 10, 0)
        });
        copy.Children.Add(new TextBlock
        {
            Text = $"{mod.Author} · {mod.Category}",
            Foreground = (Brush)FindResource("Faint"), FontSize = 10.5, Margin = new Thickness(0, 3, 10, 0)
        });
        Grid.SetColumn(copy, 2);
        grid.Children.Add(copy);

        var facts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        facts.Children.Add(new TextBlock
        {
            Text = $"★ {(string.IsNullOrWhiteSpace(mod.Rating) ? "—" : mod.Rating)}   ↓ {(string.IsNullOrWhiteSpace(mod.Downloads) ? "—" : mod.Downloads)}",
            Foreground = (Brush)FindResource("Accent"), FontSize = 11.5
        });
        facts.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(mod.Updated) ? "" : "Updated " + mod.Updated,
            Foreground = (Brush)FindResource("Faint"), FontSize = 10.5, Margin = new Thickness(0, 3, 0, 0)
        });
        Grid.SetColumn(facts, 3);
        grid.Children.Add(facts);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var view = new Button { Content = "View", Style = (Style)FindResource("Small"), Margin = new Thickness(0, 0, 7, 0) };
        view.Click += (_, _) => OpenOfficial(mod.DetailsUri.ToString());
        var download = new Button { Content = "Download", Style = (Style)FindResource("Primary") };
        download.Click += async (_, _) => await DownloadRepositoryModAsync(mod, download);
        actions.Children.Add(view);
        actions.Children.Add(download);
        Grid.SetColumn(actions, 4);
        grid.Children.Add(actions);

        return new Border
        {
            BorderBrush = (Brush)FindResource("Line"),
            BorderThickness = new Thickness(0, RepoList.Items.Count == 0 ? 0 : 1, 0, 0),
            Child = grid
        };
    }

    private UIElement BuildRepositoryThumbnail(OfficialMod mod)
    {
        var host = new Grid { Width = 112, Height = 72, VerticalAlignment = VerticalAlignment.Center };
        host.Children.Add(new Border
        {
            Background = (Brush)FindResource("BgAlt"),
            BorderBrush = (Brush)FindResource("Line"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Child = new TextBlock
            {
                Text = "BEAMNG\nMOD",
                Foreground = (Brush)FindResource("Faint"),
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        });
        if (mod.ImageUri is null) return host;

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.UriSource = mod.ImageUri;
        bitmap.DecodePixelWidth = 224;
        bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        bitmap.EndInit();
        var image = new Image
        {
            Source = bitmap,
            Stretch = Stretch.UniformToFill,
            Width = 112,
            Height = 72
        };
        image.ImageFailed += (_, _) => image.Visibility = Visibility.Collapsed;
        var clip = new RectangleGeometry(new System.Windows.Rect(0, 0, 112, 72), 7, 7);
        image.Clip = clip;
        host.Children.Add(image);
        return host;
    }

    private async Task DownloadRepositoryModAsync(OfficialMod mod, Button button)
    {
        button.IsEnabled = false;
        var original = button.Content;
        try
        {
            button.Content = "Starting…";
            var progress = new Progress<RepoDownloadProgress>(value =>
            {
                button.Content = value.TotalBytes is > 0
                    ? $"{value.Percent:0}%"
                    : Size(value.BytesReceived);
            });
            var path = await OfficialModRepository.DownloadAsync(mod, progress);
            button.Content = "Installed";
            var cfg = _state.Config;
            cfg.UseRepositoryMods = true;
            ChkRepo.IsChecked = true;
            _state.Save();
            var playerCount = Math.Max(Instances.CountBuilt(cfg), cfg.Players.Count);
            await Task.Run(() => ModManager.Apply(cfg, playerCount, _state.Progress()));
            Scan();
            LblStatus.Text = $"Downloaded {Path.GetFileName(path)} from the official BeamNG repository and linked it to {playerCount} profile(s).";
        }
        catch (Exception ex)
        {
            button.Content = "Retry";
            LblRepoStatus.Text = $"Download failed for {mod.Title}: {ex.Message}";
            _state.Log($"Official mod download failed: {mod.Title} · {ex.Message}");
        }
        finally
        {
            button.IsEnabled = true;
            if (Equals(button.Content, "Starting…")) button.Content = original;
        }
    }

    private static void OpenOfficial(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !uri.Host.Equals("www.beamng.com", StringComparison.OrdinalIgnoreCase)) return;
        Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
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
            Title = "Choose a shared BeamNG mod library",
            Multiselect = false
        };
        if (Directory.Exists(TxtSource.Text)) dlg.InitialDirectory = TxtSource.Text;
        if (dlg.ShowDialog() != true) return;
        TxtSource.Text = dlg.FolderName;
        Scan();
    }

    private void Scan(IReadOnlySet<string>? selectedServerPaths = null)
    {
        var cfg = _state.Config;
        var packages = ModManager.DiscoverConfigured(cfg, TxtSource.Text.Trim());
        var serverSet = selectedServerPaths is null
            ? cfg.ServerModFiles.ToHashSet(StringComparer.OrdinalIgnoreCase)
            : null;

        _rows.Clear();
        ModList.Items.Clear();
        foreach (var package in packages)
        {
            var server = new CheckBox
            {
                IsChecked = selectedServerPaths is null
                    ? serverSet!.Contains(package.RelativePath)
                    : selectedServerPaths.Contains(package.FullPath),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0)
            };
            var grid = new Grid { MinHeight = 43 };
            grid.ColumnDefinitions.Add(new ColumnDefinition());
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
            Grid.SetColumn(server, 1);
            grid.Children.Add(packageCell);
            grid.Children.Add(server);
            var border = new Border
            {
                BorderBrush = (Brush)FindResource("Line"),
                BorderThickness = new Thickness(0, ModList.Items.Count == 0 ? 0 : 1, 0, 0),
                Child = grid
            };
            ModList.Items.Add(border);
            _rows.Add(new ModRow(package, server));
        }

        LblEmpty.Visibility = packages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        LblEmpty.Text = Directory.Exists(TxtSource.Text.Trim()) || Directory.Exists(ModManager.RepositorySource)
            ? "No ZIP mods found. BeamSplit ignores multiplayer downloads so it cannot re-share BeamMP.zip."
            : "Choose your BeamNG mods folder, or download something from the official repository above.";
        var total = packages.Sum(p => p.Bytes);
        LblStatus.Text = packages.Count == 0
            ? "Nothing to apply yet."
            : $"Available libraries contain {packages.Count} package(s), {Size(total)} total. Tick only the packages the BeamMP server should distribute.";
    }

    private async Task ApplyAsync()
    {
        var source = TxtSource.Text.Trim();
        // Keep what is currently checked in the UI. ResolvePlayerSource can narrow a
        // selected BeamNG user folder to mods\repo, and the required rescan must not
        // restore the older saved selection before this Apply has persisted it.
        var selectedServerPaths = _rows
            .Where(r => r.Server.IsChecked == true)
            .Select(r => r.Package.FullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(source) && !Directory.Exists(source))
        {
            LblStatus.Text = "Choose a valid mods folder first.";
            return;
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(source))
            {
                source = ModManager.ResolvePlayerSource(source);
                TxtSource.Text = source;
            }
            Scan(selectedServerPaths);
        }
        catch (Exception ex)
        {
            LblStatus.Text = ex.Message;
            return;
        }

        var cfg = _state.Config;
        cfg.ModsSourceDir = string.IsNullOrWhiteSpace(source) ? null : source;
        cfg.ModsConfigured = !string.IsNullOrWhiteSpace(source);
        cfg.UsePlayerMods = ChkPlayers.IsChecked == true;
        cfg.UseRepositoryMods = ChkRepo.IsChecked == true;
        cfg.PlayerModFiles.Clear(); // legacy v1.6.0 per-package copy selections
        cfg.ServerModFiles = _rows.Where(r => r.Server.IsChecked == true).Select(r => r.Package.RelativePath).ToList();
        _state.Save();

        BtnApply.IsEnabled = false;
        LblStatus.Text = "Synchronising selected packages...";
        try
        {
            var playerCount = Math.Max(Instances.CountBuilt(cfg), cfg.Players.Count);
            await Task.Run(() => ModManager.Apply(cfg, playerCount, _state.Progress()));
            _state.Save();
            var selectedCount = cfg.ServerModFiles.Count;
            var managedCount = cfg.ManagedServerModFiles.Count;
            var serverSummary = selectedCount == 0
                ? "no server packages selected"
                : managedCount == selectedCount
                    ? $"{selectedCount} server package(s) selected and deployed"
                    : !Directory.Exists(cfg.ServerDir)
                        ? $"{selectedCount} server package(s) selected; configure the server folder to deploy them"
                        : $"{selectedCount} server package(s) selected; check Console for existing-name or copy warnings";
            LblStatus.Text = $"Applied: shared library {(cfg.UsePlayerMods ? $"linked to {playerCount} profile(s) with no copies" : "off")}, " +
                             $"official downloads {(cfg.UseRepositoryMods ? "on" : "off")}, {serverSummary}." +
                             (ServerConfig.IsRunning() ? " Restart the server to publish its new list." : "");
        }
        catch (Exception ex)
        {
            LblStatus.Text = "Mod sync stopped: " + ex.Message;
            _state.Log("Mod sync failed: " + ex.Message);
        }
        finally { BtnApply.IsEnabled = true; }
    }

    private void SetServerChecks(bool value)
    {
        foreach (var row in _rows) row.Server.IsChecked = value;
    }

    private static string Size(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024d * 1024 * 1024):0.0} GB",
        >= 1024L * 1024 => $"{bytes / (1024d * 1024):0.0} MB",
        >= 1024L => $"{bytes / 1024d:0} KB",
        _ => $"{bytes} B"
    };

    private sealed record ModRow(ModPackage Package, CheckBox Server);
}
