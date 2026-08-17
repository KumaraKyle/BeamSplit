using Microsoft.Win32;

namespace BeamSplit.Core;

/// <summary>
/// Enumerates active Windows render endpoints without adding an audio package to the
/// portable build. Endpoint friendly names are the same strings BeamNG stores in its
/// AudioDevice setting and reports in beamng.log.
/// </summary>
public static class AudioDevices
{
    private const string RenderKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render";
    private const string EndpointNameProperty = "{a45c254e-df1c-4efd-8020-67d146a850e0},2";
    private const string InterfaceNameProperty = "{b3f8fa53-0004-438e-9003-51a46e139bfc},6";

    public static string? LastError { get; private set; }

    public static IReadOnlyList<string> GetRenderDeviceNames()
    {
        LastError = null;
        var names = new List<string>();
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(RenderKey);
            if (root == null) return names;

            foreach (var endpointId in root.GetSubKeyNames())
            {
                using var endpoint = root.OpenSubKey(endpointId);
                // 1 = DEVICE_STATE_ACTIVE. Ignore disconnected and disabled outputs.
                if (endpoint?.GetValue("DeviceState") is int state && state != 1) continue;

                using var properties = endpoint?.OpenSubKey("Properties");
                if (properties?.GetValue(EndpointNameProperty) is not string endpointName ||
                    string.IsNullOrWhiteSpace(endpointName)) continue;

                var interfaceName = properties.GetValue(InterfaceNameProperty) as string;
                var name = string.IsNullOrWhiteSpace(interfaceName) ||
                           endpointName.Equals(interfaceName, StringComparison.OrdinalIgnoreCase)
                    ? endpointName
                    : $"{endpointName} ({interfaceName})";

                if (!names.Contains(name, StringComparer.OrdinalIgnoreCase)) names.Add(name);
            }
        }
        catch (Exception ex)
        {
            // The combo remains editable if endpoint discovery is unavailable.
            LastError = ex.Message;
        }

        names.Sort(StringComparer.CurrentCultureIgnoreCase);
        return names;
    }
}
