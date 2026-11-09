using Hearth.Core;
using Hearth.Core.Models;
using Hearth.UI.State;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MudBlazor.Services;

namespace Hearth.UI;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers everything the shared UI needs. Register host overrides (IVoiceInput…) before calling.</summary>
    public static IServiceCollection AddHearth(this IServiceCollection services, PlatformInfo platform)
    {
        services.AddHearthCore(platform);
        services.AddScoped<AppState>();
        services.TryAddScoped<IVoiceInput, BrowserVoiceInput>();
        services.TryAddScoped<ISpeechOutput, BrowserSpeechOutput>();
        services.AddScoped<LiveVoice>();
        services.AddMudServices();
        return services;
    }
}
