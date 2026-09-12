using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace GameHours.Windows.Notifications;

public sealed class WindowsAppNotificationTransport : IDisposable
{
    private readonly Action _activated;
    private AppNotificationManager? _manager;

    public WindowsAppNotificationTransport(Action activated) =>
        _activated = activated ?? throw new ArgumentNullException(nameof(activated));

    public bool TryRegister()
    {
        if (_manager is not null) return true;

        AppNotificationManager? manager = null;
        try
        {
            if (!AppNotificationManager.IsSupported()) return false;
            manager = AppNotificationManager.Default;
            manager.NotificationInvoked += NotificationInvoked;
            manager.Register();
            _manager = manager;
            return true;
        }
        catch
        {
            if (manager is not null)
            {
                try { manager.NotificationInvoked -= NotificationInvoked; } catch { }
                try { manager.Unregister(); } catch { }
            }
            return false;
        }
    }

    public bool TryShow(string title, IReadOnlyList<string> bodyLines)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(bodyLines);
        if (_manager is null) return false;

        try
        {
            var builder = new AppNotificationBuilder()
                .AddArgument("action", "open")
                .AddText(title);
            foreach (var line in bodyLines.Where(line => !string.IsNullOrWhiteSpace(line)))
            {
                builder.AddText(line);
            }

            _manager.Show(builder.BuildNotification());
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void NotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args) =>
        _activated();

    public void Dispose()
    {
        var manager = _manager;
        _manager = null;
        if (manager is null) return;

        try { manager.NotificationInvoked -= NotificationInvoked; } catch { }
        try { manager.Unregister(); } catch { }
    }
}
