using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;

namespace BeamSplit.Core;

/// <summary>How one player's screen area is defined.</summary>
public enum SplitMode { Full = 1, TwoStacked = 2, TwoSideBySide = 3, FourGrid = 4 }

/// <summary>Which local multiplayer architecture launches the session.</summary>
public enum SessionEngine
{
    MultiInstance = 0,
    SingleInstanceExperimental = 1
}

/// <summary>
/// One player: which monitor, which region of it, and which physical pad.
/// Monitors are keyed by DeviceName, never by index - enumeration order is not
/// stable (the primary display moved between index 0 and 2 during development).
/// </summary>
public sealed class PlayerSlot
{
    public int Index { get; set; }                 // instance number: P0, P1, ...
    public string MonitorDevice { get; set; } = "";
    public SplitMode Split { get; set; } = SplitMode.Full;
    public int Region { get; set; }                // 0-based region within the split
    public int Pad { get; set; } = -1;             // physical XInput index, -1 = none
    public bool Keyboard { get; set; }
}

/// <summary>A DirectInput controller we've seen before, so it can be hidden even when asleep.</summary>
public sealed class CachedPad
{
    public int Index { get; set; }
    public string Guid { get; set; } = "";
    public string Name { get; set; } = "";
}

public sealed class AppConfig
{
    public bool OnboardingComplete { get; set; }
    public int OnboardingStep { get; set; }
    public bool AppTourComplete { get; set; }
    public bool LaunchCinematic { get; set; } = true;
    public bool AutoUpdateCheck { get; set; } = true;
    public DateTime? LastUpdateCheckUtc { get; set; }
    public string? SkippedUpdateVersion { get; set; }

    public string? GameRoot { get; set; }
    public string? LauncherExe { get; set; }
    public string? ServerDir { get; set; }
    public string? ModZip { get; set; }
    public string InstancesDir { get; set; } = Paths.DefaultInstancesDir;

    // Personal/community mods are read from this folder. Player profiles mount it by
    // directory junction (zero copies); the source folder is never changed by BeamSplit.
    public string? ModsSourceDir { get; set; }
    public bool ModsConfigured { get; set; }
    public bool UsePlayerMods { get; set; }
    public bool UseRepositoryMods { get; set; } = true;
    public List<string> PlayerModFiles { get; set; } = []; // v1.6.0 migration only
    public List<string> ServerModFiles { get; set; } = [];
    // Destination names written by BeamSplit into Resources/Client. This lets later
    // syncs remove only BeamSplit-owned files and leave hand-installed server mods alone.
    public List<string> ManagedServerModFiles { get; set; } = [];

    /// <summary>Instance N uses BasePort + N*2 - see Launcher for why they are spaced.</summary>
    public int BasePort { get; set; } = 4444;

    public string Mode { get; set; } = "BeamMP";   // or "Solo"
    public SessionEngine SessionEngine { get; set; } = SessionEngine.MultiInstance;
    public bool AutoJoinBeamMp { get; set; } = true;// guest login + local direct connect
    public bool Borderless { get; set; } = true;
    public bool Isolate { get; set; } = true;      // per-instance controller isolation
    public bool UseProtoInput { get; set; } = true;// injected focus-independent controller routing
    public bool Watchdog { get; set; } = false;    // legacy fallback: keep game windows unfocused
    public int FrameLimit { get; set; } = 60;      // same cap in foreground and background

    // BeamNG audio settings applied to every profile immediately before launch.
    // The Windows volume mixer remains independent and can still mute/reroute an
    // individual BeamNG process after it has started.
    public int AudioMaster { get; set; } = 100;
    public int AudioEffects { get; set; } = 80;
    public int AudioMusic { get; set; } = 80;
    public int AudioUi { get; set; } = 80;
    public bool AudioInBackground { get; set; } = true;
    public bool AudioStereoHeadphones { get; set; }
    public string? AudioDevice { get; set; }        // null = Windows default output
    /// <summary>LocalVehicle (recommended), All, or P0Only.</summary>
    public string AudioMixMode { get; set; } = "LocalVehicle";
    /// <summary>
    /// Optional Goldberg Steam-API emulation for the instance copies. Off by default,
    /// and BeamSplit never downloads it - the user points at their own copy.
    /// </summary>
    public bool UseSteamEmu { get; set; }
    public string? SteamEmuPath { get; set; }

    public bool ApplyGraphics { get; set; }
    public bool LowMemoryGraphics { get; set; } = true;
    public int Aniso { get; set; } = 4;
    public int AntiAlias { get; set; } = 1;
    public bool NoShadows { get; set; }

    public List<PlayerSlot> Players { get; set; } = [];

    /// <summary>
    /// Remembered DirectInput controllers (index, instance GUID, name).
    ///
    /// devreorder hides pads by GUID, and wireless pads vanish from enumeration when
    /// they idle out - so without a cache, deploying while the pads are asleep silently
    /// skips DirectInput filtering. That is exactly how an instance ends up responding
    /// to every pad the moment its window gains focus.
    /// </summary>
    public List<CachedPad> KnownPads { get; set; } = [];

    [JsonIgnore]
    public int PlayerCount => Math.Max(Players.Count, 0);

    /// <summary>
    /// Ensures the requested number of slots without replacing layouts already made on
    /// the Screens page. The old implementation rebuilt every slot immediately before
    /// launch, silently turning a vertical split back into one full-screen window per
    /// monitor. Defaults are now created only for genuinely new players.
    /// </summary>
    public void EnsureDefaultPlayers(int count)
    {
        count = Math.Max(1, count);
        var monitors = Native.GetMonitors();
        if (monitors.Count == 0) return;

        if (Players.Count > count)
            Players = Players.Take(count).ToList();

        while (Players.Count < count)
        {
            var index = Players.Count;
            var unused = monitors.FirstOrDefault(m =>
                Players.All(p => !p.MonitorDevice.Equals(m.DeviceName, StringComparison.OrdinalIgnoreCase)));

            if (!string.IsNullOrWhiteSpace(unused.DeviceName))
            {
                Players.Add(new PlayerSlot
                {
                    Index = index,
                    MonitorDevice = unused.DeviceName,
                    Split = SplitMode.Full,
                    Region = 0,
                    Pad = index
                });
                continue;
            }

            // Every display already has a player: add the new one to the primary and
            // expand only that display's existing slots to the required capacity.
            var primary = monitors.FirstOrDefault(m => m.Primary, monitors[0]);
            var onPrimary = Players
                .Where(p => p.MonitorDevice.Equals(primary.DeviceName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var mode = onPrimary.Count + 1 <= 2 ? SplitMode.TwoStacked : SplitMode.FourGrid;
            for (var region = 0; region < onPrimary.Count; region++)
            {
                onPrimary[region].Split = mode;
                onPrimary[region].Region = region;
            }
            Players.Add(new PlayerSlot
            {
                Index = index,
                MonitorDevice = primary.DeviceName,
                Split = mode,
                Region = onPrimary.Count,
                Pad = index
            });
        }

        // A new launch creates fresh P0..Pn processes, so normalize only their identity;
        // monitor, split, region, pad and keyboard choices remain untouched.
        for (var i = 0; i < Players.Count; i++) Players[i].Index = i;
    }
}

public static class Paths
{
    public static string AppData { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BeamSplit");

    public static string ConfigFile => Path.Combine(AppData, "config.json");
    public static string ConfigBackupFile => Path.Combine(AppData, "config.json.backup");
    public static string BinDir => Path.Combine(AppData, "bin");
    public static string ModsDir => Path.Combine(AppData, "mods");
    public static string ProtoInputDir => Path.Combine(BinDir, "protoinput");
    public static string ServerDirDefault => Path.Combine(AppData, "server");
    public static string DefaultInstancesDir => Path.Combine(AppData, "instances");
    public static string LogFile => Path.Combine(AppData, "beamsplit.log");

    public static void EnsureAll()
    {
        foreach (var d in new[] { AppData, BinDir, ModsDir, ProtoInputDir })
            Directory.CreateDirectory(d);
    }
}

public static class ConfigStore
{
    private static readonly object SaveGate = new();
    public static string? LastLoadNotice { get; private set; }
    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static AppConfig Load()
    {
        Paths.EnsureAll();
        LastLoadNotice = null;
        foreach (var candidate in new[] { Paths.ConfigFile, Paths.ConfigBackupFile })
        {
            try
            {
                if (!File.Exists(candidate)) continue;
                var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(candidate), Opts);
                if (cfg is null) continue;
                Normalize(cfg);
                if (candidate == Paths.ConfigBackupFile)
                {
                    LastLoadNotice = "The main config was unreadable; BeamSplit restored its backup.";
                    // Preserve the known-good backup while atomically replacing only the
                    // corrupt primary. The old primary is retained for diagnosis.
                    WriteAtomic(Paths.ConfigFile, Paths.ConfigFile + ".corrupt",
                        JsonSerializer.Serialize(cfg, Opts));
                }
                return cfg;
            }
            catch { /* try the backup, then safe defaults */ }
        }

        if (File.Exists(Paths.ConfigFile) || File.Exists(Paths.ConfigBackupFile))
            LastLoadNotice = "Both config copies were unreadable; BeamSplit created safe defaults.";
        var fresh = new AppConfig();
        Detect.FillMissing(fresh);
        Normalize(fresh);
        Save(fresh);
        return fresh;
    }

    /// <summary>
    /// Keep instances on the same volume as the game.
    ///
    /// Two reasons this matters: an instance costs ~500MB (its own Bin64), and the root
    /// files are HARDLINKED from the install - hardlinks cannot cross volumes, so an
    /// instance on another drive silently becomes a full copy of everything instead.
    /// The system drive is usually the tightest one, too.
    /// </summary>
    public static void Normalize(AppConfig cfg)
    {
        cfg.Players ??= [];
        cfg.KnownPads ??= [];
        cfg.PlayerModFiles ??= [];
        cfg.ServerModFiles ??= [];
        cfg.ManagedServerModFiles ??= [];
        if (!Enum.IsDefined(cfg.SessionEngine)) cfg.SessionEngine = SessionEngine.MultiInstance;
        if (cfg.SessionEngine == SessionEngine.SingleInstanceExperimental)
            cfg.Mode = "Solo";
        if (!new[] { "LocalVehicle", "All", "P0Only" }.Contains(cfg.AudioMixMode,
                StringComparer.OrdinalIgnoreCase))
            cfg.AudioMixMode = "LocalVehicle";

        if (!Detect.IsGameRoot(cfg.GameRoot)) return;

        var gameVolume = Path.GetPathRoot(cfg.GameRoot!);
        var instVolume = Path.GetPathRoot(cfg.InstancesDir);
        if (gameVolume is null || instVolume is null) return;

        var onAppData = string.Equals(cfg.InstancesDir.TrimEnd('\\'),
                                      Paths.DefaultInstancesDir.TrimEnd('\\'),
                                      StringComparison.OrdinalIgnoreCase);

        if (onAppData && !string.Equals(gameVolume, instVolume, StringComparison.OrdinalIgnoreCase))
            cfg.InstancesDir = Path.Combine(gameVolume, "BeamSplit", "instances");
    }

    public static void Save(AppConfig cfg)
    {
        Paths.EnsureAll();
        WriteAtomic(Paths.ConfigFile, Paths.ConfigBackupFile, JsonSerializer.Serialize(cfg, Opts));
    }

    internal static void WriteAtomic(string destination, string backup, string contents)
    {
        lock (SaveGate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var temp = destination + $".{Guid.NewGuid():N}.tmp";
            try
            {
                using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(fs, new UTF8Encoding(false)))
                {
                    writer.Write(contents);
                    writer.Flush();
                    fs.Flush(true);
                }
                if (File.Exists(destination)) File.Replace(temp, destination, backup, true);
                else File.Move(temp, destination);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
        }
    }
}
