namespace Locus.Core.Services;

/// <summary>Provided by the web host so the UI can explain how to reach Locus from a phone.</summary>
public interface IAccessInfo
{
    IReadOnlyList<string> ListenUrls { get; }
    bool PasswordSet { get; }
}
