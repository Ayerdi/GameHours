using System.Runtime.InteropServices;
using GameHours.Core.Abstractions;

namespace GameHours.Windows.Input;

public sealed class WindowsUserInteractionStateProvider : IUserInteractionStateProvider
{
    private const uint ErrorSuccess = 0;
    private const int ControllerSlotCount = 4;
    private const long DisconnectedControllerRetryMilliseconds = 5_000;

    private readonly uint?[] _lastControllerPackets = new uint?[ControllerSlotCount];
    private readonly bool[] _controllerConnected = new bool[ControllerSlotCount];
    private readonly long[] _nextDisconnectedControllerProbeTick = new long[ControllerSlotCount];
    private long? _lastControllerInteractionTick;
    private bool _xInputAvailable = true;

    public ValueTask<UserInteractionState> GetStateAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var foregroundProcessId = GetForegroundProcessId();
        var keyboardMouseIdle = GetKeyboardMouseIdleDuration();
        var controllerIdle = GetControllerIdleDuration();
        var idle = keyboardMouseIdle <= controllerIdle ? keyboardMouseIdle : controllerIdle;

        return ValueTask.FromResult(new UserInteractionState(foregroundProcessId, idle));
    }

    private static int? GetForegroundProcessId()
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero) return null;

        _ = GetWindowThreadProcessId(window, out var processId);
        return processId == 0 || processId > int.MaxValue ? null : (int)processId;
    }

    private static TimeSpan GetKeyboardMouseIdleDuration()
    {
        var info = new LastInputInfo
        {
            Size = (uint)Marshal.SizeOf<LastInputInfo>()
        };

        if (!GetLastInputInfo(ref info)) return TimeSpan.MaxValue;

        // LASTINPUTINFO uses the 32-bit system tick counter. Unsigned subtraction intentionally
        // handles wrap-around. We only compare the result with a small idle threshold.
        var now = unchecked((uint)Environment.TickCount);
        var elapsedMilliseconds = unchecked(now - info.TickCount);
        return TimeSpan.FromMilliseconds(elapsedMilliseconds);
    }

    private TimeSpan GetControllerIdleDuration()
    {
        if (!_xInputAvailable) return TimeSpan.MaxValue;

        var now = Environment.TickCount64;
        try
        {
            for (var slot = 0; slot < ControllerSlotCount; slot++)
            {
                if (!_controllerConnected[slot] && now < _nextDisconnectedControllerProbeTick[slot])
                {
                    continue;
                }

                var result = XInputGetState((uint)slot, out var state);
                if (result != ErrorSuccess)
                {
                    _controllerConnected[slot] = false;
                    _lastControllerPackets[slot] = null;
                    _nextDisconnectedControllerProbeTick[slot] =
                        now + DisconnectedControllerRetryMilliseconds;
                    continue;
                }

                _controllerConnected[slot] = true;
                _nextDisconnectedControllerProbeTick[slot] = 0;

                var previousPacket = _lastControllerPackets[slot];
                _lastControllerPackets[slot] = state.PacketNumber;
                if (previousPacket.HasValue && previousPacket.Value != state.PacketNumber)
                {
                    _lastControllerInteractionTick = now;
                }
            }
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            _xInputAvailable = false;
            return TimeSpan.MaxValue;
        }

        if (_lastControllerInteractionTick is not long lastTick) return TimeSpan.MaxValue;
        var elapsed = Math.Max(0, now - lastTick);
        return TimeSpan.FromMilliseconds(elapsed);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint TickCount;
    }

    // XINPUT_STATE is 16 bytes: a 4-byte packet number followed by a 12-byte gamepad payload.
    // GameHours deliberately exposes only the packet number to managed code. The remaining bytes
    // are opaque native output and are never represented as buttons, triggers or stick values.
    [StructLayout(LayoutKind.Sequential, Size = 16)]
    private struct XInputState
    {
        public uint PacketNumber;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo lastInputInfo);

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern uint XInputGetState(uint userIndex, out XInputState state);
}
