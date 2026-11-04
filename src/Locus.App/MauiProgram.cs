using Locus.Core.Models;
using Locus.Core.Services;
using Locus.UI;
using Locus.UI.State;
using Microsoft.Extensions.Logging;

namespace Locus.App;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts => fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular"));

        builder.Services.AddMauiBlazorWebView();

        var platform = DeviceInfo.Current.Platform;
        var isMobile = platform == DevicePlatform.Android || platform == DevicePlatform.iOS;
        var isAndroidEmulator = platform == DevicePlatform.Android && DeviceInfo.Current.DeviceType == DeviceType.Virtual;
        var canRunProcesses = platform == DevicePlatform.WinUI || platform == DevicePlatform.MacCatalyst;

        // Native implementations registered before AddLocus take precedence over the browser defaults.
        builder.Services.AddSingleton<IAppPaths, MauiAppPaths>();
        builder.Services.AddScoped<IVoiceInput, MauiVoiceInput>();
        builder.Services.AddScoped<ISpeechOutput, MauiSpeechOutput>();
        builder.Services.AddLocus(new PlatformInfo($"{platform} {DeviceInfo.Current.VersionString}",
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
