using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using BeamSplit.Core;

namespace BeamSplit.Views;

public partial class SettingsPage : UserControl
{
    private readonly AppState _state = AppState.Current;
    private readonly Func<int, Task> _rebuild;
    private readonly Func<Task> _previewCinematic;
    private UpdateInfo? _availableUpdate;

    public SettingsPage(Func<int, Task> rebuild, Func<Task> previewCinematic)
    {
        InitializeComponent();
        _rebuild = rebuild;
        _previewCinematic = previewCinematic;

        BtnSave.Click += (_, _) => Save();
        BtnRescanGames.Click += (_, _) => LoadGameRoots();
        BtnBrowseGame.Click += (_, _) => BrowseFolder("Pick your BeamNG.drive folder", p =>
        {
            if (Detect.IsGameRoot(p)) { AddGameRoot(p, select: true); }
            else _state.Log($"{p} has no Bin64\\BeamNG.drive.x64.exe");
        });
        BtnBrowseInstances.Click += (_, _) => BrowseFolder("Pick a folder for the instances", p => TxtInstances.Text = p);
        BtnOpenData.Click += (_, _) => Process.Start("explorer.exe", Paths.AppData);
        BtnResetProfiles.Click += (_, _) => ResetProfiles();
        BtnRebuild.Click += async (_, _) =>
        {
            Save();
            LblMaint.Text = "Rebuilding instances - this copies Bin64 per player and takes a while...";
            await _rebuild(Math.Max(1, _state.Config.Players.Count));
        };

        CbGameRoot.SelectionChanged += (_, _) => UpdateHints();
        TxtInstances.TextChanged += (_, _) => UpdateHints();
        BtnRefreshAudio.Click += (_, _) => LoadAudioDevices();
        BtnPreviewCinematic.Click += async (_, _) => await _previewCinematic();
        BtnCheckUpdate.Click += async (_, _) => await CheckForUpdateAsync();
        BtnInstallUpdate.Click += async (_, _) => await InstallUpdateAsync();
        BtnOpenRelease.Click += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(_availableUpdate?.ReleaseUrl))
                Process.Start(new ProcessStartInfo(_availableUpdate.ReleaseUrl) { UseShellExecute = true });
        };

        // --- Steam API emulation (optional, off by default) ---
        BtnBrowseEmu.Click += (_, _) => BrowseFolder("Pick the Goldberg folder containing steam_api64.dll", p =>
        {
            TxtSteamEmu.Text = p;
            UpdateEmuState();
        });
        BtnFindEmu.Click += (_, _) =>
        {
            var found = SteamEmu.FindExisting();
            if (found != null) { TxtSteamEmu.Text = found; _state.Log($"Found a Goldberg copy: {found}"); }
            else _state.Log("No Goldberg copy found on this PC - point BeamSplit at your own with Browse.");
            UpdateEmuState();
        };
        BtnRestoreEmu.Click += (_, _) =>
        {
            SteamEmu.RestoreAll(_state.Config, _state.Progress());
            _state.Config.UseSteamEmu = false;
            ChkSteamEmu.IsChecked = false;
            _state.Save();
            UpdateEmuState();
        };
        ChkSteamEmu.Checked += (_, _) => UpdateEmuState();
        ChkSteamEmu.Unchecked += (_, _) => UpdateEmuState();
        TxtSteamEmu.TextChanged += (_, _) => UpdateEmuState();

        Load();
    }

    private void Load()
    {
        var c = _state.Config;
        LoadGameRoots();
        TxtInstances.Text = c.InstancesDir;
        TxtLauncher.Text = c.LauncherExe ?? "";
        TxtServerDir.Text = c.ServerDir ?? "";
        TxtModZip.Text = c.ModZip ?? "";
        TxtBasePort.Text = c.BasePort.ToString();

        ChkBorderless.IsChecked = c.Borderless;
        ChkIsolate.IsChecked = c.Isolate;
        ChkProtoInput.IsChecked = c.UseProtoInput;
        ChkWatchdog.IsChecked = c.Watchdog;
        ChkLaunchCinematic.IsChecked = c.LaunchCinematic;
        ChkAutoJoinBeamMp.IsChecked = c.AutoJoinBeamMp;
        TxtFrameLimit.Text = c.FrameLimit.ToString();
        CbMode.SelectedIndex = c.Mode == "Solo" ? 1 : 0;

        TxtAudioMaster.Text = c.AudioMaster.ToString();
        TxtAudioEffects.Text = c.AudioEffects.ToString();
        TxtAudioMusic.Text = c.AudioMusic.ToString();
        TxtAudioUi.Text = c.AudioUi.ToString();
        ChkAudioBackground.IsChecked = c.AudioInBackground;
        ChkAudioHeadphones.IsChecked = c.AudioStereoHeadphones;
        CbAudioMix.SelectedIndex = c.AudioMixMode switch
        {
            "All" => 1,
            "P0Only" => 2,
            _ => 0
        };
        LoadAudioDevices();

        ChkApplyGfx.IsChecked = c.ApplyGraphics;
        CbAniso.SelectedIndex = c.Aniso switch { 0 => 0, 2 => 1, 4 => 2, 8 => 3, 16 => 4, _ => 2 };
        CbAA.SelectedIndex = Math.Clamp(c.AntiAlias, 0, 3);
        ChkNoShadows.IsChecked = c.NoShadows;

        ChkSteamEmu.IsChecked = c.UseSteamEmu;
        TxtSteamEmu.Text = c.SteamEmuPath ?? "";

        ChkAutoUpdates.IsChecked = c.AutoUpdateCheck;
        LblCurrentVersion.Text = $"Portable build {AppUpdater.CurrentVersion.ToString(3)}";
        LblUpdateStatus.Text = c.LastUpdateCheckUtc == null
            ? "Ready to check the release channel."
            : $"Last checked {c.LastUpdateCheckUtc.Value.ToLocalTime():g}.";

        UpdateHints();
        UpdateEmuState();
    }

    /// <summary>
    /// Says plainly whether emulation is usable, already applied, or a bad idea here.
    /// </summary>
    private void UpdateEmuState()
    {
        var c = _state.Config;
        var path = TxtSteamEmu.Text.Trim();
        var valid = SteamEmu.LooksLikeGoldberg(path);
        var applied = Enumerable.Range(0, 8).Any(i => SteamEmu.IsApplied(c, i));

        BtnRestoreEmu.IsEnabled = applied;

        var parts = new List<string>();

        if (path.Length == 0) parts.Add("No folder set.");
        else if (!valid) parts.Add("That folder has no steam_api64.dll - point at the Goldberg build's x64 folder.");
        else parts.Add("Goldberg found.");

        if (applied) parts.Add("Currently applied to at least one instance; the originals are backed up.");

        // Most relevant warning for this machine: the install already has a
        // third-party steam_api64 replacement, so adding another usually breaks it.
        if (SteamEmu.AlreadyPatched(c))
            parts.Add("NOTE: your game's Bin64 already contains a third-party steam_api64 replacement (OnlineFix or similar). Stacking Goldberg on top of that normally stops the game starting - you probably don't need this option at all.");

        LblEmuState.Text = string.Join("  ", parts);
    }

    private void LoadGameRoots()
    {
        var current = _state.Config.GameRoot;
        CbGameRoot.Items.Clear();
        foreach (var p in Detect.FindAllBeamNG()) CbGameRoot.Items.Add(p);
        if (current != null && !CbGameRoot.Items.Contains(current)) CbGameRoot.Items.Insert(0, current);
        CbGameRoot.SelectedItem = current ?? (CbGameRoot.Items.Count > 0 ? CbGameRoot.Items[0] : null);
        UpdateHints();
    }

    private void AddGameRoot(string path, bool select)
    {
        if (!CbGameRoot.Items.Contains(path)) CbGameRoot.Items.Insert(0, path);
        if (select) CbGameRoot.SelectedItem = path;
    }

    private void UpdateHints()
    {
        var count = CbGameRoot.Items.Count;
        LblGameHint.Text = count > 1
            ? $"{count} installs found. BeamNG rewrites its own installPath every time a copy is launched, so pick the one you actually play - instances are built from it."
            : "Detected automatically.";

        // hardlinks can't cross volumes, and an instance on another drive silently
        // becomes a full copy of everything instead of ~500MB
        var game = CbGameRoot.SelectedItem as string;
        var inst = TxtInstances.Text;
        if (!string.IsNullOrWhiteSpace(game) && !string.IsNullOrWhiteSpace(inst))
        {
            var gv = Path.GetPathRoot(game);
            var iv = Path.GetPathRoot(inst);
            LblInstHint.Text = string.Equals(gv, iv, StringComparison.OrdinalIgnoreCase)
                ? "Same drive as the game - root files are hardlinked, so each instance costs about 500MB."
                : $"Different drive from the game ({gv} vs {iv}). Hardlinks can't cross volumes, so every instance will be a FULL copy instead of ~500MB.";
        }
    }

    private void Save()
    {
        var c = _state.Config;
        if (CbGameRoot.SelectedItem is string g && Detect.IsGameRoot(g)) c.GameRoot = g;
        if (!string.IsNullOrWhiteSpace(TxtInstances.Text)) c.InstancesDir = TxtInstances.Text.Trim();
        c.LauncherExe = Blank(TxtLauncher.Text);
        c.ServerDir = Blank(TxtServerDir.Text);
        c.ModZip = Blank(TxtModZip.Text);
        if (int.TryParse(TxtBasePort.Text, out var bp) && bp is > 1024 and < 65000) c.BasePort = bp;

        c.Borderless = ChkBorderless.IsChecked == true;
        c.Isolate = ChkIsolate.IsChecked == true;
        c.UseProtoInput = ChkProtoInput.IsChecked == true;
        c.Watchdog = ChkWatchdog.IsChecked == true;
        c.LaunchCinematic = ChkLaunchCinematic.IsChecked == true;
        c.AutoJoinBeamMp = ChkAutoJoinBeamMp.IsChecked == true;
        if (int.TryParse(TxtFrameLimit.Text, out var fps)) c.FrameLimit = Math.Clamp(fps, 30, 240);
        c.Mode = CbMode.SelectedIndex == 1 ? "Solo" : "BeamMP";

        c.AudioMaster = Percent(TxtAudioMaster.Text, c.AudioMaster);
        c.AudioEffects = Percent(TxtAudioEffects.Text, c.AudioEffects);
        c.AudioMusic = Percent(TxtAudioMusic.Text, c.AudioMusic);
        c.AudioUi = Percent(TxtAudioUi.Text, c.AudioUi);
        c.AudioInBackground = ChkAudioBackground.IsChecked == true;
        c.AudioStereoHeadphones = ChkAudioHeadphones.IsChecked == true;
        c.AudioMixMode = CbAudioMix.SelectedIndex switch
        {
            1 => "All",
            2 => "P0Only",
            _ => "LocalVehicle"
        };
        var audioDevice = CbAudioDevice.Text.Trim();
        c.AudioDevice = audioDevice.Equals("System default", StringComparison.OrdinalIgnoreCase)
            ? null
            : Blank(audioDevice);

        // Steam emulation: refuse to enable it without a valid folder, rather than
        // silently doing nothing at launch time.
        c.SteamEmuPath = Blank(TxtSteamEmu.Text);
        var wantEmu = ChkSteamEmu.IsChecked == true;
        if (wantEmu && !SteamEmu.LooksLikeGoldberg(c.SteamEmuPath))
        {
            wantEmu = false;
            ChkSteamEmu.IsChecked = false;
            _state.Log("Steam emulation left off: no steam_api64.dll in the folder given.");
        }
        c.UseSteamEmu = wantEmu;

        c.ApplyGraphics = ChkApplyGfx.IsChecked == true;
        c.Aniso = CbAniso.SelectedIndex switch { 0 => 0, 1 => 2, 2 => 4, 3 => 8, _ => 16 };
        c.AntiAlias = Math.Max(0, CbAA.SelectedIndex);
        c.NoShadows = ChkNoShadows.IsChecked == true;
        c.AutoUpdateCheck = ChkAutoUpdates.IsChecked == true;

        _state.Save();
        _state.Log("Settings saved.");
        LblMaint.Text = "Saved.";
        UpdateHints();
    }

    private async Task CheckForUpdateAsync()
    {
        BtnCheckUpdate.IsEnabled = false;
        BtnInstallUpdate.IsEnabled = false;
        UpdateProgress.Value = 12;
        LblUpdateStatus.Text = "Checking the official release channel...";
        try
        {
            _availableUpdate = await AppUpdater.CheckAsync();
            _state.Config.LastUpdateCheckUtc = DateTime.UtcNow;
            _state.Save();
            LblUpdateStatus.Text = _availableUpdate.Status;
            BtnInstallUpdate.IsEnabled = _availableUpdate.Available;
            BtnOpenRelease.IsEnabled = !string.IsNullOrWhiteSpace(_availableUpdate.ReleaseUrl);
            UpdateProgress.Value = _availableUpdate.Available ? 100 : 0;
        }
        catch (Exception ex)
        {
            LblUpdateStatus.Text = $"Could not check for updates: {ex.Message}";
            UpdateProgress.Value = 0;
        }
        finally { BtnCheckUpdate.IsEnabled = true; }
    }

    private async Task InstallUpdateAsync()
    {
        if (_availableUpdate is not { Available: true, Latest: not null } update) return;
        BtnCheckUpdate.IsEnabled = BtnInstallUpdate.IsEnabled = false;
        LblUpdateStatus.Text = $"Downloading BeamSplit {update.Latest}...";
        UpdateProgress.Value = 0;
        try
        {
            var progress = new Progress<double>(value => UpdateProgress.Value = value);
            var staged = await AppUpdater.DownloadAndStageAsync(update, progress);
            LblUpdateStatus.Text = "Download verified. Restarting into the update...";
            AppUpdater.ApplyAndRestart(staged, update.Latest);
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            LblUpdateStatus.Text = $"Update stopped safely: {ex.Message}";
            BtnCheckUpdate.IsEnabled = true;
            BtnInstallUpdate.IsEnabled = true;
        }
    }

    private static string? Blank(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static int Percent(string text, int fallback) =>
        int.TryParse(text, out var value) ? Math.Clamp(value, 0, 100) : fallback;

    private void LoadAudioDevices()
    {
        var selected = _state.Config.AudioDevice;
        CbAudioDevice.Items.Clear();
        CbAudioDevice.Items.Add("System default");
        foreach (var name in AudioDevices.GetRenderDeviceNames()) CbAudioDevice.Items.Add(name);

        if (!string.IsNullOrWhiteSpace(selected) && !CbAudioDevice.Items.Contains(selected))
            CbAudioDevice.Items.Add(selected);
        CbAudioDevice.Text = string.IsNullOrWhiteSpace(selected) ? "System default" : selected;
    }

    private void ResetProfiles()
    {
        var answer = MessageBox.Show(
            "Delete every instance profile? Settings, key bindings and downloaded mods for each player are removed. The game folders are kept.",
            "BeamSplit", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        var n = 0;
        for (var i = 0; i < 8; i++)
        {
            if (!Directory.Exists(Instances.UserPath(_state.Config, i))) continue;
            Instances.ResetProfile(_state.Config, i);
            n++;
        }
        LblMaint.Text = $"Reset {n} profile(s). The next launch will be slow while they rebuild.";
        _state.Log($"Reset {n} instance profile(s).");
    }

    /// <summary>Folder picker without a WinForms dependency.</summary>
    private static void BrowseFolder(string title, Action<string> onPicked)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = title, Multiselect = false };
        if (dlg.ShowDialog() == true) onPicked(dlg.FolderName);
    }
}
