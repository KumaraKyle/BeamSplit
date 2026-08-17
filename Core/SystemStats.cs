using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace BeamSplit.Core;

public readonly record struct MachineSnapshot(
    string Cpu, string Gpu, string Os, int Threads,
    long TotalMemoryMb, long UsedMemoryMb, string Displays);

/// <summary>Small, dependency-free snapshot used by the launch dashboard.</summary>
public static class SystemStats
{
    private static readonly Lazy<string> CpuName = new(ReadCpuName);
    private static readonly Lazy<string> GpuName = new(() => ReadWmiName("Win32_VideoController"));

    public static MachineSnapshot Capture()
    {
        var (total, available) = Native.GetPhysicalMemory();

        var displays = Native.GetMonitors();
        return new MachineSnapshot(
            CpuName.Value,
            GpuName.Value,
            RuntimeInformation.OSDescription,
            Environment.ProcessorCount,
            (long)(total / 1024 / 1024),
            (long)((total - Math.Min(total, available)) / 1024 / 1024),
            string.Join("  ·  ", displays.Select(m =>
                $"{m.DeviceName.Replace(@"\\.\", "")} {m.Width}×{m.Height}")));
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
