using System.Diagnostics;
using System.IO;
using BeamSplit.Core;
using System.Windows;

namespace BeamSplit;

public partial class App : Application
{
    /// <summary>Minimum time the splash stays up, so it never just flickers.</summary>
    private static readonly TimeSpan MinSplash = TimeSpan.FromMilliseconds(1200);

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Headless diagnostics: BeamSplit.exe --selftest <file>
        // Dumps detection + setup status without showing UI. Used to verify the
        // Core layer against a real install, and handy when something breaks.
        if (e.Args.Length >= 1 && e.Args[0] == "--selftest")
        {
            var outFile = e.Args.Length > 1 ? e.Args[1] : Path.Combine(Path.GetTempPath(), "beamsplit-selftest.txt");
            NativeAssets.Extract();
            var passed = await SelfTest.RunAsync(outFile);
            Shutdown(passed ? 0 : 1);
            return;
        }

        // Read-only live probe for support/release validation:
        //   BeamSplit.exe --repotest [outfile]
        if (e.Args.Length >= 1 && e.Args[0] == "--repotest")
        {
            var outFile = e.Args.Length > 1 ? e.Args[1] : Path.Combine(Path.GetTempPath(), "beamsplit-repotest.txt");
            try
            {
                var mods = await OfficialModRepository.BrowseAsync("", 1);
                await File.WriteAllLinesAsync(outFile,
                    [$"Official BeamNG repository: {mods.Count} resources", .. mods.Take(5).Select(mod => $"{mod.Title} | {mod.Author} | {mod.DetailsUri}")]);
                Shutdown(mods.Count > 0 ? 0 : 1);
            }
            catch (Exception ex)
            {
                await File.WriteAllTextAsync(outFile, "Repository probe failed: " + ex);
                Shutdown(1);
            }
            return;
        }

        // Deploy the input isolation to every built instance and report what landed,
        // without launching anything:  BeamSplit.exe --input
        if (e.Args.Length >= 1 && e.Args[0] == "--input")
        {
            var st = AppState.Current;
            st.Load();
            var prog = new Progress<string>(s => st.Log(s));
            NativeAssets.Extract(prog);
            InputSetup.Deploy(st.Config, prog);
            for (var i = 0; i < Math.Max(1, st.Config.Players.Count); i++)
            {
                if (!Instances.Exists(st.Config, i)) continue;
                var missing = InputSetup.Verify(st.Config, i);
                st.Log($"P{i}: {(missing.Count == 0 ? "complete" : "MISSING " + string.Join(", ", missing))}");
            }
            Shutdown(0);
            return;
        }

        // Repair reused instance Bin64 folders without stopping or launching a session:
        //   BeamSplit.exe --repair [players]
        if (e.Args.Length >= 1 && e.Args[0] == "--repair")
        {
            var state = AppState.Current;
            state.Load();
            var players = e.Args.Length > 1 && int.TryParse(e.Args[1], out var n)
                ? Math.Max(1, n)
                : Math.Max(1, state.Config.Players.Count);
            var progress = new Progress<string>(s => state.Log(s));
            try
            {
                Instances.EnsureBuilt(state.Config, players, progress);
                state.Log($"Instance repair complete ({players} checked).");
            }
            catch (Exception ex)
            {
                state.Log("INSTANCE REPAIR FAILED: " + ex);
                Shutdown(1);
                return;
            }
            Shutdown(0);
            return;
        }

        // Headless launch, so the pipeline can be exercised before the Screens page
        // exists (and useful for scripting later):
        //   BeamSplit.exe --launch [players] [Solo|BeamMP]
        if (e.Args.Length >= 1 && e.Args[0] == "--launch")
        {
            var players = e.Args.Length > 1 && int.TryParse(e.Args[1], out var n) ? n : 2;
            var state = AppState.Current;
            state.Load();
            if (e.Args.Length > 2) state.Config.Mode = e.Args[2];
            state.Config.EnsureDefaultPlayers(players);
            state.Save();

            var progress = new Progress<string>(s => state.Log(s));
            try { await new Launcher(state).LaunchAsync(progress); }
            catch (Exception ex) { state.Log("LAUNCH FAILED: " + ex); }
            Shutdown(0);
            return;
        }

        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var splash = new SplashWindow();
        splash.Show();
        var clock = Stopwatch.StartNew();

        try
        {
            // Real startup work goes here as Core lands (detection, catalog check).
            // Kept off the UI thread so the splash animation stays smooth.
            await Task.Run(() =>
            {
                splash.SetStep("Loading configuration...");
                AppState.Current.Load();

                splash.SetStep("Unpacking input proxy...");
                NativeAssets.Extract(AppState.Current.Progress());

                splash.SetStep("Looking for BeamNG.drive...");
                if (!Detect.IsGameRoot(AppState.Current.Config.GameRoot))
                {
                    AppState.Current.Config.GameRoot = Detect.FindBeamNG();
                    AppState.Current.Save();
                }

                splash.SetStep("Checking controllers...");
                for (uint i = 0; i < 4; i++) Native.PadConnected(i);
            });

            var remaining = MinSplash - clock.Elapsed;
            if (remaining > TimeSpan.Zero) await Task.Delay(remaining);

            var main = new MainWindow();
            MainWindow = main;
            main.Show();
            await splash.FadeOutAsync();
            ShutdownMode = ShutdownMode.OnMainWindowClose;
        }
        catch (Exception ex)
        {
            splash.Close();
            MessageBox.Show($"BeamSplit failed to start:\n\n{ex}", "BeamSplit",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}
