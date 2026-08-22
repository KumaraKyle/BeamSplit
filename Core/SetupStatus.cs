using System.IO;

namespace BeamSplit.Core;

public sealed class SetupItem
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public bool Ok { get; set; }
    public string Detail { get; set; } = "";
    public string Action { get; set; } = "";   // "" = nothing the app can do automatically
    public bool Essential { get; set; } = true;
}

public static class SetupStatus
{
    /// <summary>Everything BeamSplit needs, in the order the user should deal with it.</summary>
    public static List<SetupItem> Evaluate(AppConfig cfg)
    {
        var version = Detect.GameVersion(cfg);
        var major = Detect.GameMajor(version);
        var items = new List<SetupItem>();

        var gameOk = Detect.IsGameRoot(cfg.GameRoot);
        var detail = gameOk ? $"{cfg.GameRoot}   (v{version})" : "not found - use Detect, or set it on Settings";

        // Warn about multiple installs. BeamNG rewrites installPath in its ini on every
        // launch, so auto-detection can point at a different copy than the one the
        // instances were built from - which would run the wrong game.
        if (gameOk)
        {
            var all = Detect.FindAllBeamNG();
            if (all.Count > 1)
                detail += $"   -  {all.Count} installs found, using this one (choose on Settings)";
        }

        items.Add(new SetupItem
        {
            Key = "game",
            Name = "BeamNG.drive",
            Ok = gameOk,
            Detail = detail,
            Action = "Detect"
        });

        var single = cfg.SessionEngine == SessionEngine.SingleInstanceExperimental;
        var beamMp = !single && cfg.Mode == "BeamMP";

        var singleCapability = SingleInstanceSupport.CheckCapability(cfg);
        items.Add(new SetupItem
        {
            Key = "singleinstance",
            Name = "Single-instance APIs",
            Ok = singleCapability.Supported,
            Detail = singleCapability.Detail,
            Essential = single
        });

        var launcherOk = !string.IsNullOrWhiteSpace(cfg.LauncherExe) && File.Exists(cfg.LauncherExe);
        items.Add(new SetupItem
        {
            Key = "launcher",
            Name = "BeamMP launcher",
            Ok = launcherOk,
            Detail = launcherOk ? cfg.LauncherExe! : "not found - install BeamMP, or switch to Solo mode",
            Action = "Detect",
            Essential = beamMp
        });

        var modOk = false;
        var modDetail = "not set";
        if (!string.IsNullOrWhiteSpace(cfg.ModZip) && File.Exists(cfg.ModZip))
        {
            var target = BeamMpCatalog.ModTargetVersion(cfg.ModZip);
            modOk = target == major && major > 0;
            modDetail = $"{Path.GetFileName(cfg.ModZip)} targets 0.{target}.x"
                        + (modOk ? "" : $"  -  MISMATCH, game is 0.{major}.x");
        }
        items.Add(new SetupItem
        {
            Key = "mod",
            Name = "BeamMP client version",
            Ok = modOk,
            Detail = modDetail,
            Action = "Fetch",
            Essential = beamMp
        });

        var serverOk = !string.IsNullOrWhiteSpace(cfg.ServerDir)
                       && File.Exists(Path.Combine(cfg.ServerDir!, "BeamMP-Server.exe"));
        items.Add(new SetupItem
        {
            Key = "server",
            Name = "BeamMP server",
            Ok = serverOk,
            Detail = serverOk ? cfg.ServerDir! : "not installed",
            Action = "Download",
            Essential = beamMp
        });

        var keyOk = serverOk && ServerConfig.HasAuthKey(cfg);
        items.Add(new SetupItem
        {
            Key = "authkey",
            Name = "Server AuthKey",
            Ok = keyOk,
            Detail = keyOk
                ? "present"
                : "required even for a private LAN server - free from keymaster.beammp.com",
            Action = "Get key",
            Essential = beamMp
        });

        var proxyOk = File.Exists(Path.Combine(Paths.BinDir, "xinput1_4.dll"))
                      && File.Exists(Path.Combine(Paths.BinDir, "dilist.exe"));
        items.Add(new SetupItem
        {
            Key = "proxy",
            Name = "Input proxy",
            Ok = proxyOk,
            Detail = proxyOk ? "installed" : "not extracted yet",
            Action = "Extract",
            Essential = !single
        });

        var protoOk = NativeAssets.ProtoInputReady;
        items.Add(new SetupItem
        {
            Key = "protoinput",
            Name = "Proto Input",
            Ok = protoOk,
            Detail = protoOk
                ? "installed - focus-independent controller routing"
                : "not extracted yet - legacy focus guard will be used",
            Action = "Extract",
            Essential = false
        });

        // Optional: without it pads are still separated through the XInput proxy,
        // the game just also lists the other controllers.
        var drOk = File.Exists(Path.Combine(Paths.BinDir, "dinput8.dll"));
        items.Add(new SetupItem
        {
            Key = "devreorder",
            Name = "devreorder (optional)",
            Ok = drOk,
            Detail = drOk
                ? "installed"
                : "missing - pads still separate, but the game lists all of them",
            Action = "Locate",
            Essential = false
        });

        var count = Directory.Exists(cfg.InstancesDir)
            ? Directory.GetDirectories(cfg.InstancesDir, "P*").Length
            : 0;
        items.Add(new SetupItem
        {
            Key = "instances",
            Name = "Game instances",
            Ok = count > 0,
            Detail = count > 0
                ? $"{count} in {cfg.InstancesDir}"
                : "none yet - built on first launch (about 500MB each)",
            Action = "Build",
            Essential = false
        });

        return items;
    }

    /// <summary>Blocking problems only - what stops a launch.</summary>
    public static List<string> Blockers(AppConfig cfg) =>
        Evaluate(cfg).Where(i => i.Essential && !i.Ok).Select(i => $"{i.Name}: {i.Detail}").ToList();
}
