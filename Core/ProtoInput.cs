using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace BeamSplit.Core;

/// <summary>Managed binding to Proto Input's public 64-bit C API.</summary>
public static class ProtoInput
{
    private enum Hook : uint { Focus = 10, XInput = 12 }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private unsafe delegate uint InjectStartup(string exe, string commandLine, uint flags,
        string dllFolder, uint* pid, IntPtr environment);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void SetupState(uint h, int index);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void InstallHook(uint h, Hook hook);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void SetControllerIndex(uint h, uint a, uint b, uint c, uint d);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void SetBool(uint h, [MarshalAs(UnmanagedType.I1)] bool enabled);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void StartFocusLoop(uint h, int ms,
        [MarshalAs(UnmanagedType.I1)] bool activate, [MarshalAs(UnmanagedType.I1)] bool activateApp,
        [MarshalAs(UnmanagedType.I1)] bool ncActivate, [MarshalAs(UnmanagedType.I1)] bool setFocus,
        [MarshalAs(UnmanagedType.I1)] bool mouseActivate);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Wake(uint h);

    private static readonly object Gate = new();
    private static IntPtr _library;
    private static InjectStartup? _inject;
    private static SetupState? _setup;
    private static InstallHook? _hook;
    private static SetControllerIndex? _controller;
    private static SetBool? _openXInput;
    private static StartFocusLoop? _focusLoop;
    private static Wake? _wake;
    private static readonly Dictionary<int, (uint Handle, int Pid)> Active = [];

    public static bool Ready => NativeAssets.ProtoInputReady;

    public static unsafe Process? Start(AppConfig cfg, int instance, string exe,
        IProgress<string>? log = null, bool useOpenXInput = true, string commandLine = "")
    {
        if (!cfg.UseProtoInput || !Ready) return null;
        var slot = cfg.Players.ElementAtOrDefault(instance);
        if (slot is null || slot.Keyboard || slot.Pad < 0) return null;

        lock (Gate)
        {
            try
            {
                EnsureLoaded();
                // Apps launched from a packaged shell (Codex, Explorer aliases, etc.) can
                // inherit a WindowsApps working directory which exists but cannot be set
                // again. A failed restore used to throw after injection and make Launcher
                // start an unisolated fallback process for that player. Always restore to
                // an accessible directory, and never let restoration invalidate a
                // successful injection.
                var restoreCwd = AppContext.BaseDirectory;
                try
                {
                    var inherited = Environment.CurrentDirectory;
                    if (!string.IsNullOrWhiteSpace(inherited)) restoreCwd = inherited;
                }
                catch { }
                uint pid = 0;
                uint handle;
                try
                {
                    Environment.CurrentDirectory = Instances.Bin64(cfg, instance);
                    var runtimeDir = Paths.ProtoInputDir.TrimEnd(Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    handle = _inject!(exe, commandLine, 0, runtimeDir, &pid, IntPtr.Zero);
                }
                finally
                {
                    try { Environment.CurrentDirectory = restoreCwd; }
                    catch { Environment.CurrentDirectory = AppContext.BaseDirectory; }
                }

                if (handle == 0 || pid == 0) throw new InvalidOperationException("injection returned no process");
                _setup!(handle, instance + 1);
                _openXInput!(handle, useOpenXInput);
                _controller!(handle, (uint)slot.Pad + 1, 0, 0, 0);
                _hook!(handle, Hook.XInput);
                _hook!(handle, Hook.Focus);
                _focusLoop!(handle, 5, true, true, true, true, true);
                _wake!(handle);
                Active[instance] = (handle, (int)pid);
                log?.Report($"  P{instance}: Proto Input pad {slot.Pad}, fake focus, pid {pid}");
                return Process.GetProcessById((int)pid);
            }
            catch (Exception ex)
            {
                log?.Report($"  P{instance}: Proto Input unavailable ({ex.Message}); using legacy launch");
                return null;
            }
        }
    }

    /// <summary>Changes a running injected instance without relaunching it.</summary>
    public static bool SetPad(int instance, int pad)
    {
        lock (Gate)
        {
            if (!Active.TryGetValue(instance, out var active)) return false;
            try
            {
                using var process = Process.GetProcessById(active.Pid);
                if (process.HasExited) { Active.Remove(instance); return false; }
                EnsureLoaded();
                _controller!(active.Handle, (uint)pad + 1, 0, 0, 0);
                return true;
            }
            catch
            {
                Active.Remove(instance);
                return false;
            }
        }
    }

    private static void EnsureLoaded()
    {
        if (_library != IntPtr.Zero) return;
        _library = NativeLibrary.Load(Path.Combine(Paths.ProtoInputDir, "ProtoInputLoader64.dll"));
        _inject = Export<InjectStartup>("EasyHookInjectStartup");
        _setup = Export<SetupState>("SetupState");
        _hook = Export<InstallHook>("InstallHook");
        _controller = Export<SetControllerIndex>("SetControllerIndex");
        _openXInput = Export<SetBool>("SetUseOpenXinput");
        _focusLoop = Export<StartFocusLoop>("StartFocusMessageLoop");
        _wake = Export<Wake>("WakeUpProcess");
    }

    private static T Export<T>(string name) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_library, name));
}
