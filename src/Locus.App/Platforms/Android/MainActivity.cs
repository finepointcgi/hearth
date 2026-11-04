using Android.App;
using Android.Content.PM;
using Android.OS;

namespace Locus.App;

[IntentFilter([Android.Content.Intent.ActionView], Categories = [Android.Content.Intent.CategoryDefault, Android.Content.Intent.CategoryBrowsable], DataScheme = "locus")]
[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTask, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
}
