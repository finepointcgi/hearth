using Hearth.Core.Models;

namespace Hearth.Core.Tests;

public class PrivacyTests
{
    [Theory]
    [InlineData(ProviderKind.Ollama, "http://localhost:11434", true)]
    [InlineData(ProviderKind.OpenAICompatible, "http://192.168.1.20:1234/v1", true)]
    [InlineData(ProviderKind.OpenAICompatible, "http://gpubox:8000/v1", true)]
    [InlineData(ProviderKind.OpenAICompatible, "http://100.101.5.9:11434", true)]
    [InlineData(ProviderKind.OpenAICompatible, "https://api.openai.com/v1", false)]
    [InlineData(ProviderKind.OpenAICompatible, "https://openrouter.ai/api/v1", false)]
    [InlineData(ProviderKind.Anthropic, "https://api.anthropic.com", false)]
    [InlineData(ProviderKind.ClaudeCode, "", false)]
    public void DetectsLocalProviders(ProviderKind kind, string url, bool local) =>
        Assert.Equal(local, Personas.IsLocal(new ProviderProfile { Kind = kind, BaseUrl = url }));

    [Fact]
    public void CloudPromptDoesNotClaimPrivacy()
    {
        var prompt = Personas.Render(Personas.Default, null, new ProviderProfile { Kind = ProviderKind.ClaudeCode });
        Assert.DoesNotContain("nothing the user says leaves", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Anthropic", prompt);
        Assert.Contains("nothing the user says leaves",
            Personas.Render(Personas.Default, null, new ProviderProfile { Kind = ProviderKind.Ollama, BaseUrl = "http://localhost:11434" }));
    }
}
