using Microsoft.Win32;

namespace GameHours.Desktop;

public sealed class WindowsStartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "GameHours";

    public bool IsSupported => !string.IsNullOrWhiteSpace(Environment.ProcessPath);

    public bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is string value &&
                   string.Equals(value, BuildCommand(), StringComparison.OrdinalIgnoreCase);
        }
    }

    public void SetEnabled(bool enabled)
    {
        var command = BuildCommand();
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("Could not open the current-user Run registry key.");

        if (enabled)
        {
            key.SetValue(ValueName, command, RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    private static string BuildCommand()
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
        {
            throw new InvalidOperationException("Could not determine the GameHours executable path.");
        }

        return $"\"{executable}\" --background";
    }
}
