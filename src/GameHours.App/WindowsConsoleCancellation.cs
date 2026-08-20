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
        SetConsoleCtrlHandler(Handler, add: true);
    }

    private static bool HandleConsoleControl(uint controlType)
    {
        if (controlType is not (CtrlCEvent or CtrlBreakEvent))
        {
            return false;
        }

        // Returning true prevents the Windows default handler from terminating GameHours
        // before its async tracker shutdown has flushed the active session to SQLite.
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
