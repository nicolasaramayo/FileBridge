#if ANDROID
using Android.App;
using Android.Content;
using Android.OS;
using AndroidX.Core.App;

namespace FileBridge.Infrastructure.Services.Platform.Android;

[Service(
    Name = "com.filebridge.filetransfer",
    ForegroundServiceType = global::Android.Content.PM.ForegroundService.TypeDataSync,
    Exported = false)]
public class AndroidFileTransferService : Service
{
    public const int NotificationId = 1001;
    public const string ChannelId = "file_transfer_channel";

    private static AndroidFileTransferService? _instance;
    public static AndroidFileTransferService? Instance => _instance;

    private NotificationManager? _notificationManager;

    public override void OnCreate()
    {
        base.OnCreate();
        _instance = this;
        _notificationManager = GetSystemService(NotificationService) as NotificationManager;
        CreateNotificationChannel();
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        var notification = CreateNotification("Starting file transfer service...", 0, 0);
        StartForeground(NotificationId, notification);

        return StartCommandResult.Sticky;
    }

    public override IBinder? OnBind(Intent? intent) => null;

    public void UpdateProgress(string fileName, long transferred, long total)
    {
        if (total <= 0) return;

        var percent = (int)((transferred * 100) / total);
        var notification = CreateNotification($"{fileName} - {percent}%", transferred, total);
        _notificationManager?.Notify(NotificationId, notification);
    }

    public void ShowCompletion(string fileName)
    {
        var notification = CreateCompletionNotification(fileName);
        _notificationManager?.Notify(NotificationId, notification);
    }

    private void CreateNotificationChannel()
    {
        var channel = new NotificationChannel(
            ChannelId,
            "File Transfers",
            NotificationImportance.Default)
        {
            Description = "Shows ongoing file transfers"
        };

        _notificationManager?.CreateNotificationChannel(channel);
    }

    private Notification CreateNotification(string text, long transferred, long total)
    {
        var intent = new Intent(this, typeof(global::Android.App.Activity));
        var pendingIntent = PendingIntent.GetActivity(
            this, 0, intent, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

        var builder = new NotificationCompat.Builder(this, ChannelId)
            .SetContentTitle("FileBridge")
            .SetContentText(text)
            .SetSmallIcon(global::Android.Resource.Drawable.StatNotifyChat)
            .SetContentIntent(pendingIntent)
            .SetOngoing(true)
            .SetOnlyAlertOnce(true);

        if (total > 0)
        {
            builder.SetProgress((int)total, (int)transferred, false);
        }
        else
        {
            builder.SetProgress(0, 0, true);
        }

        return builder.Build();
    }

    private Notification CreateCompletionNotification(string fileName)
    {
        var intent = new Intent(this, typeof(global::Android.App.Activity));
        var pendingIntent = PendingIntent.GetActivity(
            this, 0, intent, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

        return new NotificationCompat.Builder(this, ChannelId)
            .SetContentTitle("Transfer Complete")
            .SetContentText($"Successfully transferred {fileName}")
            .SetSmallIcon(global::Android.Resource.Drawable.StatNotifyChat)
            .SetContentIntent(pendingIntent)
            .SetAutoCancel(true)
            .Build();
    }

    public override void OnDestroy()
    {
        _instance = null;
        base.OnDestroy();
    }
}
#endif
