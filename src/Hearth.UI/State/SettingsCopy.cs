using System.Text.Json;
using System.Text.Json.Serialization;
using Hearth.Core.Models;

namespace Hearth.UI.State;

/// <summary>Settings pages edit a deep copy so unsaved changes don't leak into the running app.</summary>
public static class SettingsCopy
{
    private static readonly JsonSerializerOptions Options = new() { Converters = { new JsonStringEnumConverter() } };

    public static AppSettings Of(AppSettings s) =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(s, Options), Options)!;
}
