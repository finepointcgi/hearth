using System.Runtime.InteropServices;
using Locus.UI;
using Locus.Core.Models;
using Locus.Core.Services;
using Locus.Computer;
using Microsoft.Extensions.DependencyInjection;
using Photino.Blazor;

namespace Locus.Desktop;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var os = OperatingSystem.IsLinux() ? "Linux" : OperatingSystem.IsWindows() ? "Windows" : "macOS";

        Locus.Core.Services.BackupService.ApplyPendingRestore(new Locus.Core.Services.DefaultAppPaths().DataDirectory);
        var builder = PhotinoBlazorAppBuilder.CreateDefault(args);
        const int webhookPort = 5299;
        builder.Services.AddLocus(new PlatformInfo($"{os} ({RuntimeInformation.OSArchitecture})", IsMobile: false,
            WebhookBaseUrl: $"http://localhost:{webhookPort}/"));
        builder.Services.AddLocusComputer();
        builder.RootComponents.Add<LocusRoutes>("#app");

        var app = builder.Build();

        // Routines, folder watchers and a localhost-only webhook endpoint run while the app is open.
        app.Services.GetRequiredService<RoutineRunner>().EnsureStarted();
        app.Services.GetRequiredService<WebhookListener>().Start(webhookPort, onNetwork: false);
        app.MainWindow
            .SetLogVerbosity(0)
            .SetTitle("Locus")
            .SetSize(1240, 820)
            .SetMinSize(420, 520)
            .SetUseOsDefaultSize(false)
            .Center();

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            app.MainWindow.ShowMessage("Locus crashed", e.ExceptionObject.ToString());

        app.Run();
    }
}
