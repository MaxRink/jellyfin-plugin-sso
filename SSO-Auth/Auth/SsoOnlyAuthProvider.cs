using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Authentication;

namespace Jellyfin.Plugin.SSO_Auth.Auth;

/// <summary>
/// An authentication provider that never authenticates. Accounts owned by this provider can only
/// sign in through SSO, because no password will ever be accepted for them.
/// </summary>
public class SsoOnlyAuthProvider : IAuthenticationProvider
{
    /// <inheritdoc />
    public string Name => "SSO Only (password login disabled)";

    /// <inheritdoc />
    public bool IsEnabled => true;

    /// <inheritdoc />
    public Task<ProviderAuthenticationResult> Authenticate(string username, string password)
    {
        throw new AuthenticationException("Password login is disabled for this account; sign in through SSO.");
    }

    /// <inheritdoc />
    public Task ChangePassword(User user, string newPassword)
    {
        throw new AuthenticationException("Password changes are disabled while SSO-only login is enforced for this account.");
    }
}
