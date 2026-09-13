namespace GameHours.Desktop;

internal static class DesktopSaveSafetyOperationLifetime
{
    private static readonly object Gate = new();
    private static int _activeOperations;
    private static bool _stopping;
    private static TaskCompletionSource? _idleCompletion;

    public static bool TryBegin(out IDisposable? lease)
    {
        lock (Gate)
        {
            if (_stopping)
            {
                lease = null;
                return false;
            }

            _activeOperations++;
            lease = new OperationLease();
            return true;
        }
    }

    public static Task StopAndWaitAsync()
    {
        lock (Gate)
        {
            _stopping = true;
            if (_activeOperations == 0)
            {
                return Task.CompletedTask;
            }

            _idleCompletion ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return _idleCompletion.Task;
        }
    }

    public static void Resume()
    {
        lock (Gate)
        {
            _stopping = false;
        }
    }

    private sealed class OperationLease : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            lock (Gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _activeOperations--;
                if (_activeOperations == 0)
                {
                    _idleCompletion?.TrySetResult();
                    _idleCompletion = null;
                }
            }
        }
    }
}
