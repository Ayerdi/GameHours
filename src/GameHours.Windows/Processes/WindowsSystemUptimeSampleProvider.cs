using System.Runtime.InteropServices;
using GameHours.Core.Monitoring;

namespace GameHours.Windows.Processes;

public sealed class WindowsSystemUptimeSampleProvider
{
    public bool TryGetSample(out SystemUptimeSample? sample)
    {
        sample = null;
        if (!QueryUnbiasedInterruptTime(out var unbiased100Ns) ||
            unbiased100Ns > long.MaxValue)
        {
            return false;
        }

        var observedAt = DateTimeOffset.UtcNow;
        var biased = TimeSpan.FromMilliseconds(GetTickCount64());
        var unbiased = TimeSpan.FromTicks((long)unbiased100Ns);
        sample = new SystemUptimeSample(observedAt, biased, unbiased);
        return true;
    }

    [DllImport("Kernel32.dll")]
    private static extern ulong GetTickCount64();

    [DllImport("Kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryUnbiasedInterruptTime(out ulong unbiasedTime);
}
