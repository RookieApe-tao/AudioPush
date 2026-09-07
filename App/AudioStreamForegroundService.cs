using Android.App;
using Android.Content;
using Android.OS;
using AndroidX.Core.App;

namespace Audio2PhoneApp;

[Service(ForegroundServiceType = Android.Content.PM.ForegroundService.TypeMediaPlayback)]
public class AudioStreamForegroundService : Service
{
    const string CHANNEL_ID = "audio2phone_channel";
    const int NOTIFICATION_ID = 1;
    PowerManager.WakeLock? _wakeLock;

    public override IBinder? OnBind(Intent? intent) => null;

    public override void OnCreate()
    {
        base.OnCreate();
        CreateNotificationChannel();
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        var notification = new NotificationCompat.Builder(this, CHANNEL_ID)
            .SetContentTitle("audio2phone")
            .SetContentText("正在播放电脑声音")
            .SetSmallIcon(Android.Resource.Drawable.IcMediaPlay)
            .SetOngoing(true)
            .SetPriority(NotificationCompat.PriorityLow)
            .Build();

        StartForeground(NOTIFICATION_ID, notification);

        // 部分 WakeLock：只保持 CPU 运行（不亮屏），防止 Doze 暂停网络
        try
        {
            var pm = (PowerManager?)GetSystemService(PowerService);
            _wakeLock = pm?.NewWakeLock(WakeLockFlags.Partial, "audio2phone::stream");
            _wakeLock?.Acquire();
        }
        catch { }

        return StartCommandResult.Sticky;
    }

    public override void OnDestroy()
    {
        try { _wakeLock?.Release(); } catch { }
        _wakeLock = null;
        StopForeground(StopForegroundFlags.Remove);
        base.OnDestroy();
    }

    void CreateNotificationChannel()
    {
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
        {
            var channel = new NotificationChannel(CHANNEL_ID, "audio2phone 播放",
                NotificationImportance.Low)
            {
                Description = "正在播放电脑声音时显示",
            };
            var manager = GetSystemService(NotificationService) as NotificationManager;
            manager?.CreateNotificationChannel(channel);
        }
    }
}
