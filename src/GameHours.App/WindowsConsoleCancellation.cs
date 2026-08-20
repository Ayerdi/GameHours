using System.Runtime.InteropServices;

internal sealed class WindowsConsoleCancellation : IDisposable
{
    private const uint CtrlCEvent = 0;
    private const uint CtrlBreakEvent = 1;

    private readonly CancellationTokenSource _cancellation = new();
    private readonly ConsoleCtrlHandler _handler;
    private bool _registered;
    private bool _disposed;

    public CancellationToken Token => _cancellation.Token;
    public bool IsCancellationRequested => _cancellation.IsCancellationRequested;

    public WindowsConsoleCancellation()
    {
        _handler = HandleConsoleControl;
        _registered = SetConsoleCtrlHandler(_handler, add: true);

        // Keep the managed event as a fallback for hosts that surface Ctrl+C through .NET.
        // The native handler is what prevents the Windows default handler from killing the
        // process before GameHours has flushed active tracking state.
        Console.CancelKeyPress += OnCancelKeyPress;
    }

    private bool HandleConsoleControl(uint controlType)
    {
        if (controlType is not (CtrlCEvent or CtrlBreakEvent))
        {
            return false;
        }

        RequestCancellation();
        return true;
    }

    private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs eventArgs)
    {
        eventArgs.Cancel = true;
        RequestCancellation();
    }

    private void RequestCancellation()
    {
        if (_disposed || _cancellation.IsCancellationRequested)
        {
            return;
        }

        try
        {
            _cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Console.CancelKeyPress -= OnCancelKeyPress;

        if (_registered)
        {
            SetConsoleCtrlHandler(_handler, add: false);
            _registered = false;
        }

        _cancellation.Dispose();
    }

    private delegate bool ConsoleCtrlHandler(uint controlType);

    [DllImport("Kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleCtrlHandler(
        ConsoleCtrlHandler handlerRoutine,
        [MarshalAs(UnmanagedType.Bool)] bool add);
}
