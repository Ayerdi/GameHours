namespace GameHours.Desktop;

/// <summary>
/// Holds an exclusive per-user file lease for the lifetime of the desktop process.
/// The lock lives beside GameHours user data so independently installed/published copies
/// cannot concurrently operate on the same local database.
/// </summary>
internal sealed class DesktopSingleInstanceLease : IDisposable
{
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;

    private readonly FileStream _stream;

    private DesktopSingleInstanceLease(FileStream stream)
    {
        _stream = stream;
    }

    public static DesktopSingleInstanceLease? TryAcquireDefault()
    {
        var dataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GameHours");

        return TryAcquire(Path.Combine(dataDirectory, "gamehours.instance.lock"));
    }

    internal static DesktopSingleInstanceLease? TryAcquire(string lockFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lockFilePath);

        var fullPath = Path.GetFullPath(lockFilePath);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("The instance lock path must have a parent directory.", nameof(lockFilePath));
        Directory.CreateDirectory(directory);

        try
        {
            var stream = new FileStream(
                fullPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.None);

            return new DesktopSingleInstanceLease(stream);
        }
        catch (IOException exception) when (IsSharingOrLockViolation(exception))
        {
            return null;
        }
    }

    public void Dispose() => _stream.Dispose();

    private static bool IsSharingOrLockViolation(IOException exception)
    {
        var nativeError = exception.HResult & 0xFFFF;
        return nativeError is ErrorSharingViolation or ErrorLockViolation;
    }
}
