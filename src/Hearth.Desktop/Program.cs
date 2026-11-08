using System.Runtime.InteropServices;
using Hearth.UI;
using Hearth.Core.Models;
using Hearth.Core.Services;
using Hearth.Computer;
using Microsoft.Extensions.DependencyInjection;
using Photino.Blazor;

namespace Hearth.Desktop;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var os = OperatingSystem.IsLinux() ? "Linux" : OperatingSystem.IsWindows() ? "Windows" : "macOS";

        Hearth.Core.Services.BackupService.ApplyPendingRestore(new Hearth.Core.Services.DefaultAppPaths().DataDirectory);
        var builder = PhotinoBlazorAppBuilder.CreateDefault(args);
        const int webhookPort = 5299;
        builder.Services.AddHearth(new PlatformInfo($"{os} ({RuntimeInformation.OSArchitecture})", IsMobile: false,
            WebhookBaseUrl: $"http://localhost:{webhookPort}/"));
        builder.Services.AddHearthComputer();
        builder.RootComponents.Add<HearthRoutes>("#app");

        var app = builder.Build();

        // Routines, folder watchers and a localhost-only webhook endpoint run while the app is open.
        app.Services.GetRequiredService<RoutineRunner>().EnsureStarted();
        app.Services.GetRequiredService<WebhookListener>().Start(webhookPort, onNetwork: false);
        app.MainWindow
            .SetLogVerbosity(0)
            .SetTitle("Hearth")
            .SetSize(1240, 820)
            .SetMinSize(420, 520)
            .SetUseOsDefaultSize(false)
            .Center();

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            app.MainWindow.ShowMessage("Hearth crashed", e.ExceptionObject.ToString());

        app.Run();
    }
}
