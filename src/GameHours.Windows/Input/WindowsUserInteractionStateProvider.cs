using System.Runtime.InteropServices;
using GameHours.Core.Abstractions;

namespace GameHours.Windows.Input;

public sealed class WindowsUserInteractionStateProvider : IUserInteractionStateProvider
{
    private const uint ErrorSuccess = 0;
    private const byte TriggerThreshold = 30;
    private const short LeftThumbDeadzone = 7849;
    private const short RightThumbDeadzone = 8689;

    private readonly uint?[] _lastControllerPackets = new uint?[4];
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
            for (uint index = 0; index < 4; index++)
            {
                var result = XInputGetState(index, out var state);
                if (result != ErrorSuccess)
                {
                    _lastControllerPackets[index] = null;
                    continue;
                }

                var previousPacket = _lastControllerPackets[index];
                _lastControllerPackets[index] = state.PacketNumber;
                if ((previousPacket.HasValue && previousPacket.Value != state.PacketNumber) ||
                    HasMeaningfulControllerState(state.Gamepad))
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

    private static bool HasMeaningfulControllerState(XInputGamepad gamepad) =>
        gamepad.Buttons != 0 ||
        gamepad.LeftTrigger > TriggerThreshold ||
        gamepad.RightTrigger > TriggerThreshold ||
        Math.Abs((int)gamepad.ThumbLX) > LeftThumbDeadzone ||
        Math.Abs((int)gamepad.ThumbLY) > LeftThumbDeadzone ||
        Math.Abs((int)gamepad.ThumbRX) > RightThumbDeadzone ||
        Math.Abs((int)gamepad.ThumbRY) > RightThumbDeadzone;

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint TickCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        public uint PacketNumber;
        public XInputGamepad Gamepad;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputGamepad
    {
        public ushort Buttons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public short ThumbLX;
        public short ThumbLY;
        public short ThumbRX;
        public short ThumbRY;
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
