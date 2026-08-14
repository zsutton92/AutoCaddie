using Android.App;
using Android.Content;
using Android.OS;
using AndroidX.Core.App;

namespace AutoCaddie.Platforms.Android;

// The ForegroundServiceType is required for modern Android versions (Android 14+)
[Service(ForegroundServiceType = global::Android.Content.PM.ForegroundService.TypeLocation)]
public class GpsForegroundService : Service
{
    private const string ChannelId = "AutoCaddieGpsChannel";
    private const int NotificationId = 1001;

    public override IBinder OnBind(Intent intent) => null;

    public override StartCommandResult OnStartCommand(Intent intent, StartCommandFlags flags, int startId)
    {
        if (intent?.Action == "STOP_SERVICE")
        {
            StopForeground(StopForegroundFlags.Remove);
            StopSelf();
            return StartCommandResult.NotSticky;
        }

        CreateNotificationChannel();

        var notification = new NotificationCompat.Builder(this, ChannelId)
            .SetContentTitle("AutoCaddie Active Round")
            .SetContentText("Tracking location for auto-scoring.")
            .SetSmallIcon(global::Android.Resource.Drawable.IcMenuMyLocation)
            .SetPriority(NotificationCompat.PriorityDefault) // BUMPED TO DEFAULT
            .SetOngoing(true)
            .Build();

        // THE ANDROID 14 FIX: Explicitly pass the Location type into the start command
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
        {
            StartForeground(NotificationId, notification, global::Android.Content.PM.ForegroundService.TypeLocation);
        }
        else
        {
            StartForeground(NotificationId, notification);
        }

        return StartCommandResult.Sticky;
    }

    private void CreateNotificationChannel()
    {
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
        {
            // BUMPED TO DEFAULT SO THE PHONE DOESN'T HIDE IT
            var channel = new NotificationChannel(ChannelId, "GPS Tracking", NotificationImportance.Default)
            {
                Description = "Keeps the GPS active while your phone is in your pocket."
            };

            var notificationManager = (NotificationManager)GetSystemService(Context.NotificationService);
            notificationManager.CreateNotificationChannel(channel);
        }
    }
}