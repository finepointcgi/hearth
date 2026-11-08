namespace Hearth.Core.Services;

/// <summary>Provided by the web host so the UI can explain how to reach Hearth from a phone.</summary>
public interface IAccessInfo
{
    IReadOnlyList<string> ListenUrls { get; }
    bool PasswordSet { get; }
    /// <summary>Visitors have to sign in: a password is set, or separate accounts are on.</summary>
    bool SignInRequired => PasswordSet;
}
