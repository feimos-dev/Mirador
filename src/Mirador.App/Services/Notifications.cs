using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace Mirador.App.Services;

public static class Notifications
{
    private static bool registered;

    public static void Register(Action clicked)
    {
        try
        {
            var manager = AppNotificationManager.Default;
            manager.NotificationInvoked += (_, _) => clicked();
            manager.Register();
            registered = true;
        }
        catch (Exception e)
        {
            AppLog.Write("Windows notifications aren't available", e);
        }
    }

    public static void Show(string title, string text)
    {
        if (!registered)
        {
            return;
        }

        try
        {
            var notification = new AppNotificationBuilder().AddText(title).AddText(text).BuildNotification();
            AppNotificationManager.Default.Show(notification);
        }
        catch (Exception e)
        {
            AppLog.Write("A notification couldn't be shown", e);
        }
    }

    public static void Unregister()
    {
        if (!registered)
        {
            return;
        }

        try
        {
            AppNotificationManager.Default.Unregister();
        }
        catch (Exception e)
        {
            AppLog.Write("Windows notifications couldn't be unregistered", e);
        }

        registered = false;
    }
}
