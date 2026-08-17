using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BeamSplit.Core;

/// <summary>How one player's screen area is defined.</summary>
public enum SplitMode { Full = 1, TwoStacked = 2, TwoSideBySide = 3, FourGrid = 4 }

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
    public string? GameRoot { get; set; }
    public string? LauncherExe { get; set; }
    public string? ServerDir { get; set; }
    public string? ModZip { get; set; }
    public string InstancesDir { get; set; } = Paths.DefaultInstancesDir;

    /// <summary>Instance N uses BasePort + N*2 - see Launcher for why they are spaced.</summary>
    public int BasePort { get; set; } = 4444;

    public string Mode { get; set; } = "BeamMP";   // or "Solo"
    public bool Borderless { get; set; } = true;
    public bool Isolate { get; set; } = true;      // per-instance controller isolation
    public bool UseProtoInput { get; set; } = true;// injected focus-independent controller routing
    public bool Watchdog { get; set; } = false;    // legacy fallback: keep game windows unfocused
    public int FrameLimit { get; set; } = 60;      // same cap in foreground and background
    /// <summary>
    /// Optional Goldberg Steam-API emulation for the instance copies. Off by default,
    /// and BeamSplit never downloads it - the user points at their own copy.
    /// </summary>
    public bool UseSteamEmu { get; set; }
    public string? SteamEmuPath { get; set; }

    public bool ApplyGraphics { get; set; }
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
    /// Sensible default assignment when the user hasn't arranged anything yet:
    /// one player per monitor while monitors last, then fill by splitting the primary.
    /// Pads map 1:1 to players. The Screens page overwrites all of this.
    /// </summary>
    public void EnsureDefaultPlayers(int count)
    {
        var monitors = Native.GetMonitors();
        if (monitors.Count == 0) return;

        var slots = new List<PlayerSlot>();
        for (var i = 0; i < count; i++)
        {
            if (i < monitors.Count)
            {
                slots.Add(new PlayerSlot
                {
                    Index = i,
                    MonitorDevice = monitors[i].DeviceName,
                    Split = SplitMode.Full,
                    Region = 0,
                    Pad = i
                });
            }
            else
            {
                // more players than screens: stack the extras on the primary
                var primary = monitors.FirstOrDefault(m => m.Primary, monitors[0]);
                var extra = i - monitors.Count;
                slots.Add(new PlayerSlot
                {
                    Index = i,
                    MonitorDevice = primary.DeviceName,
                    Split = count - monitors.Count > 1 ? SplitMode.FourGrid : SplitMode.TwoStacked,
                    Region = extra,
                    Pad = i
                });
            }
        }

        // keep any pad choices the user already made for these slots
        for (var i = 0; i < slots.Count && i < Players.Count; i++)
            if (Players[i].Pad >= 0) slots[i].Pad = Players[i].Pad;

        Players = slots;
    }
}

public static class Paths
{
    public static string AppData { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BeamSplit");

    public static string ConfigFile => Path.Combine(AppData, "config.json");
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
    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static AppConfig Load()
    {
        Paths.EnsureAll();
        if (File.Exists(Paths.ConfigFile))
        {
            try
            {
                var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(Paths.ConfigFile), Opts);
                if (cfg != null) { Normalize(cfg); return cfg; }
            }
            catch { /* corrupt config shouldn't block startup - fall through to defaults */ }
        }

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
        File.WriteAllText(Paths.ConfigFile, JsonSerializer.Serialize(cfg, Opts));
    }
}
