using Locus.Core;
using Locus.Core.Models;
using Locus.UI.State;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Locus.UI;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers everything the shared UI needs. Register host overrides (IVoiceInput…) before calling.</summary>
    public static IServiceCollection AddLocus(this IServiceCollection services, PlatformInfo platform)
    {
        services.AddLocusCore(platform);
        services.AddScoped<AppState>();
        services.TryAddScoped<IVoiceInput, BrowserVoiceInput>();
        services.TryAddScoped<ISpeechOutput, BrowserSpeechOutput>();
        services.AddScoped<LiveVoice>();
        return services;
    }
}
