using Velopack;

namespace GameHours.Update;

public static class VelopackLifecycle
{
    private static int _initialized;

    public static void Initialize()
    {
        if (Interlocked.Exchange(ref _initialized, 1) != 0)
        {
            return;
        }

        VelopackApp.Build()
            .SetAutoApplyOnStartup(false)
            .Run();
    }
}
