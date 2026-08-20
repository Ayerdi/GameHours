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
        // Console control events are only a best-effort fallback for the development CLI.
        // Interactive shells such as PowerShell participate in the same console control
        // events, so the production desktop lifecycle must never depend on Ctrl+C.
        SetConsoleCtrlHandler(Handler, add: true);
    }

    private static bool HandleConsoleControl(uint controlType)
    {
        if (controlType is not (CtrlCEvent or CtrlBreakEvent))
        {
            return false;
        }

        // Returning true asks Windows not to run its default termination handler for this
        // process. Some console hosts can still impose their own interruption semantics, so
        // this remains a fallback rather than GameHours' production shutdown contract.
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
