using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace BeamSplit.Core;

public readonly record struct MachineSnapshot(
    string Cpu, string Gpu, string Os, int Threads, double SystemLoadPercent,
    long TotalMemoryMb, long UsedMemoryMb, string Displays);

/// <summary>Small, dependency-free snapshot used by the launch dashboard.</summary>
public static class SystemStats
{
    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime { public uint Low; public uint High; }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);

    private static readonly Lazy<string> CpuName = new(ReadCpuName);
    private static readonly Lazy<string> GpuName = new(() => ReadWmiName("Win32_VideoController"));
    private static readonly object CpuLock = new();
    private static ulong _lastIdle;
    private static ulong _lastKernel;
    private static ulong _lastUser;

    public static MachineSnapshot Capture()
    {
        var (total, available) = Native.GetPhysicalMemory();

        var displays = Native.GetMonitors();
        return new MachineSnapshot(
            CpuName.Value,
            GpuName.Value,
            RuntimeInformation.OSDescription,
            Environment.ProcessorCount,
            ReadSystemLoad(),
            (long)(total / 1024 / 1024),
            (long)((total - Math.Min(total, available)) / 1024 / 1024),
            string.Join("  ·  ", displays.Select(m =>
                $"{m.DeviceName.Replace(@"\\.\", "")} {m.Width}×{m.Height}")));
    }

    private static double ReadSystemLoad()
    {
        if (!GetSystemTimes(out var idleFt, out var kernelFt, out var userFt)) return 0;
        static ulong Value(FileTime value) => ((ulong)value.High << 32) | value.Low;
        var idle = Value(idleFt);
        var kernel = Value(kernelFt);
        var user = Value(userFt);
        lock (CpuLock)
        {
            var idleDelta = idle - _lastIdle;
            var totalDelta = kernel - _lastKernel + user - _lastUser;
            _lastIdle = idle; _lastKernel = kernel; _lastUser = user;
            if (totalDelta == 0 || idleDelta > totalDelta) return 0;
            return Math.Clamp((totalDelta - idleDelta) * 100d / totalDelta, 0, 100);
        }
    }

    private static string ReadCpuName()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return key?.GetValue("ProcessorNameString")?.ToString()?.Trim() ?? "Unknown CPU";
        }
        catch { return "Unknown CPU"; }
    }

    private static string ReadWmiName(string className)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher($"SELECT Name FROM {className}");
            return searcher.Get().Cast<ManagementObject>()
                .Select(o => o["Name"]?.ToString()?.Trim())
                .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? "Unknown GPU";
        }
        catch { return "Unknown GPU"; }
    }
}
