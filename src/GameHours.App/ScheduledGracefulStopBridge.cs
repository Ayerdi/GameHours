using System.Globalization;
using System.Runtime.CompilerServices;
using GameHours.Core.Tracking;

internal static class ScheduledGracefulStopBridge
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        var args = Environment.GetCommandLineArgs();
        if (args.Length < 4 ||
            !string.Equals(args[1], "track", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        for (var index = 2; index < args.Length - 1; index++)
        {
            if (!string.Equals(args[index], "--stop-after", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!double.TryParse(
                    args[index + 1],
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var seconds) ||
                seconds <= 0 ||
                seconds > 3600)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(seconds));
                Console.WriteLine($"Scheduled graceful stop after {seconds:0.###} s...");
                GracefulShutdownSignal.Request();
            });
            return;
        }
    }
}
