using System.Runtime.InteropServices;
using GameHours.Core.Monitoring;

namespace GameHours.Windows.Processes;

public sealed class WindowsSystemUptimeSampleProvider
{
    public SystemUptimeSample GetSample()
    {
        if (!QueryUnbiasedInterruptTime(out var unbiased100Ns))
        {
            throw new InvalidOperationException(
                $"QueryUnbiasedInterruptTime failed with Win32 error {Marshal.GetLastWin32Error()}.");
        }

        if (unbiased100Ns > long.MaxValue)
        {
            throw new OverflowException("Windows unbiased uptime exceeded TimeSpan range.");
        }

        var observedAt = DateTimeOffset.UtcNow;
        var biased = TimeSpan.FromMilliseconds(GetTickCount64());
        var unbiased = TimeSpan.FromTicks((long)unbiased100Ns);
        return new SystemUptimeSample(observedAt, biased, unbiased);
    }

    [DllImport("Kernel32.dll")]
    private static extern ulong GetTickCount64();

    [DllImport("Kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryUnbiasedInterruptTime(out ulong unbiasedTime);
}
