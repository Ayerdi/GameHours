using System.Runtime.InteropServices;
using System.Text;

namespace GameHours.Windows.Srum;

public sealed class WindowsDevicePathResolver
{
    private readonly IReadOnlyList<(string DevicePrefix, string DrivePrefix)> _mappings;

    public WindowsDevicePathResolver()
    {
        _mappings = BuildMappings();
    }

    public string? Resolve(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var value = path.Trim();
        if (!value.StartsWith("\\Device\\", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                return Path.GetFullPath(value);
            }
            catch (Exception exception) when (
                exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return null;
            }
        }

        foreach (var mapping in _mappings)
        {
            if (!value.StartsWith(mapping.DevicePrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return mapping.DrivePrefix + value[mapping.DevicePrefix.Length..];
        }

        return null;
    }

    private static IReadOnlyList<(string DevicePrefix, string DrivePrefix)> BuildMappings()
    {
        var results = new List<(string DevicePrefix, string DrivePrefix)>();
        foreach (var driveRoot in Environment.GetLogicalDrives())
        {
            var drivePrefix = driveRoot.TrimEnd('\\');
            if (drivePrefix.Length != 2 || drivePrefix[1] != ':')
            {
                continue;
            }

            var buffer = new StringBuilder(1024);
            var length = QueryDosDevice(drivePrefix, buffer, buffer.Capacity);
            if (length == 0)
            {
                continue;
            }

            foreach (var target in buffer.ToString().Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!target.StartsWith("\\Device\\", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                results.Add((target, drivePrefix));
            }
        }

        return results
            .OrderByDescending(mapping => mapping.DevicePrefix.Length)
            .ToArray();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint QueryDosDevice(
        string lpDeviceName,
        StringBuilder lpTargetPath,
        int ucchMax);
}
