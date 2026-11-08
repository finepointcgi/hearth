using Hearth.Core.Services;
using Hearth.Core.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Hearth.Computer;

public static class ServiceCollectionExtensions
{
    /// <summary>Gives bots a real browser (call after AddHearth).</summary>
    public static IServiceCollection AddHearthComputer(this IServiceCollection services)
    {
        services.AddSingleton<PlaywrightComputer>();
        services.Replace(ServiceDescriptor.Singleton<IBotComputer>(sp => sp.GetRequiredService<PlaywrightComputer>()));
        services.AddSingleton<IToolProvider, ComputerToolProvider>();
        services.AddSingleton<Desktop.DesktopController>();
        services.Replace(ServiceDescriptor.Singleton<IDesktopControl>(sp => sp.GetRequiredService<Desktop.DesktopController>()));
        services.AddSingleton<LocalEmbedder>();
        services.Replace(ServiceDescriptor.Singleton<IEmbedder>(sp => sp.GetRequiredService<LocalEmbedder>()));
        services.AddSingleton<Desktop.SandboxDesktops>();
        services.Replace(ServiceDescriptor.Singleton<ISandboxDesktops>(sp => sp.GetRequiredService<Desktop.SandboxDesktops>()));
        services.AddSingleton<IToolProvider, Desktop.DesktopToolProvider>();
        return services;
    }
}
