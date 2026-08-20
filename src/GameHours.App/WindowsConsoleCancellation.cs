using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using GameHours.Core.Tracking;

internal static class WindowsConsoleShutdownBridge
{
    private const uint CtrlCEvent = 0;
    private const uint CtrlBreakEvent = 1;

    private static readonly ConsoleCtrlHandler Handler = HandleConsoleControl;

    [ModuleInitializer]
    internal static void Initialize()
    {
        // Keep native console-control handling only as a best-effort fallback. Interactive
        // shells such as PowerShell also participate in console control events, so GameHours
        // must not depend on Ctrl+C for its production lifecycle contract.
        SetConsoleCtrlHandler(Handler, add: true);

        // The development `track` host also exposes a normal-input shutdown path. Pressing
        // Enter cannot trigger the console's destructive control-signal machinery, so it is a
        // reliable end-to-end test of the same graceful signal that the desktop tray will use.
        if (IsTrackCommand() && !Console.IsInputRedirected)
        {
            _ = Task.Run(WaitForExplicitStopInput);
        }
    }

    private static void WaitForExplicitStopInput()
    {
        try
        {
            var line = Console.ReadLine();
            if (line is not null)
            {
                GracefulShutdownSignal.Request();
            }
        }
        catch (IOException)
        {
            // Console input is development-only. Losing it must never affect tracking.
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static bool IsTrackCommand()
    {
        var args = Environment.GetCommandLineArgs();
        return args.Length >= 2 &&
            string.Equals(args[1], "track", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HandleConsoleControl(uint controlType)
    {
        if (controlType is not (CtrlCEvent or CtrlBreakEvent))
        {
            return false;
        }

        // Returning true prevents the Windows default handler from terminating GameHours.
        // PowerShell/other console hosts can still have their own interruption semantics, so
        // this remains a fallback rather than the lifecycle mechanism used by the desktop app.
        GracefulShutdownSignal.Request();
        return true;
    }

    private delegate bool ConsoleCtrlHandler(uint controlType);

    [DllImport("Kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleCtrlHandler(
        ConsoleCtrlHandler handlerRoutine,
        [MarshalAs(UnmanagedType.Bool)] bool add);
}
