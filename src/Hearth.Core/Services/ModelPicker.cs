using Hearth.Core.Llm;
using Hearth.Core.Models;

namespace Hearth.Core.Services;

/// <summary>Chooses a model when no one is picking one in the UI (routines, Telegram/Discord).</summary>
public sealed class ModelPicker(SettingsStore settingsStore, ILlmClientFactory factory)
{
    public async Task<string?> PickAsync(Bot? bot)
    {
        if (!string.IsNullOrWhiteSpace(bot?.Model)) return bot.Model;
        var settings = await settingsStore.GetAsync();
        return await DefaultForAsync(factory, settings.ProviderFor(bot));
    }

    /// <summary>A server's default model, or the first one it lists.</summary>
    public static async Task<string?> DefaultForAsync(ILlmClientFactory factory, ProviderProfile provider)
    {
        if (!string.IsNullOrWhiteSpace(provider.DefaultModel)) return provider.DefaultModel;
        try { return (await factory.Create(provider).ListModelsAsync()).FirstOrDefault()?.Id; }
        catch (Exception) { return null; }
    }
}
