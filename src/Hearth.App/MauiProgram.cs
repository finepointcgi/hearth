using Hearth.Core.Models;
using Hearth.Core.Services;
using Hearth.UI;
using Hearth.UI.State;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.LifecycleEvents;

namespace Hearth.App;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        BackupService.ApplyPendingRestore(FileSystem.Current.AppDataDirectory);
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts => fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular"))
            .ConfigureLifecycleEvents(events =>
            {
                // hearth://connect?url=… links (the QR code on your computer's Phone card) open the app connected.
#if IOS || MACCATALYST
                events.AddiOS(ios => ios.OpenUrl((app, url, options) => ServerConnection.TryHandleLink(url.AbsoluteString)));
#elif ANDROID
                events.AddAndroid(android => android
                    .OnCreate((activity, _) => ServerConnection.TryHandleLink(activity.Intent?.DataString))
                    .OnNewIntent((activity, intent) => ServerConnection.TryHandleLink(intent?.DataString)));
#endif
            });

        builder.Services.AddMauiBlazorWebView();

        var platform = DeviceInfo.Current.Platform;
        var isMobile = platform == DevicePlatform.Android || platform == DevicePlatform.iOS;
        var isAndroidEmulator = platform == DevicePlatform.Android && DeviceInfo.Current.DeviceType == DeviceType.Virtual;
        var canRunProcesses = platform == DevicePlatform.WinUI || platform == DevicePlatform.MacCatalyst;

        // Native implementations registered before AddHearth take precedence over the browser defaults.
        builder.Services.AddSingleton<IAppPaths, MauiAppPaths>();
        builder.Services.AddScoped<IVoiceInput, MauiVoiceInput>();
        builder.Services.AddScoped<ISpeechOutput, MauiSpeechOutput>();
        builder.Services.AddHearth(new PlatformInfo($"{platform} {DeviceInfo.Current.VersionString}",
            isMobile, isAndroidEmulator, canRunProcesses));

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}

/// <summary>Sandbox-safe per-app storage on every platform.</summary>
internal sealed class MauiAppPaths : IAppPaths
{
    public string DataDirectory => FileSystem.Current.AppDataDirectory;
}
