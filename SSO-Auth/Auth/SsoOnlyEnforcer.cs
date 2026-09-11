using System;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SSO_Auth.Auth;

/// <summary>
/// Applies the SSO-only login setting to individual users.
/// </summary>
public static class SsoOnlyEnforcer
{
    /// <summary>
    /// The id of Jellyfin's own password authentication provider.
    /// </summary>
    public const string DefaultAuthProviderId = "Jellyfin.Server.Implementations.Users.DefaultAuthenticationProvider";

    /// <summary>
    /// Moves a user to or away from the SSO-only authentication provider, according to the plugin
    /// configuration.
    /// </summary>
    /// <remarks>
    /// Only accounts that already have an SSO link are locked to SSO. An account with no link has no
    /// way to sign in through SSO yet, so locking it would lock it out entirely; that is what keeps a
    /// break-glass administrator usable after the setting is switched on.
    /// </remarks>
    /// <param name="userManager">The user manager.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="user">The user to evaluate.</param>
    /// <returns>A task that completes once the user has been updated, if an update was needed.</returns>
    public static async Task EnforceAsync(IUserManager userManager, ILogger logger, User user)
    {
        ArgumentNullException.ThrowIfNull(userManager);
        ArgumentNullException.ThrowIfNull(user);

        var config = SSOPlugin.Instance?.Configuration;
        if (config is null)
        {
            return;
        }

        var ownedByUs = string.Equals(user.AuthenticationProviderId, typeof(SsoOnlyAuthProvider).FullName, StringComparison.Ordinal);
        var shouldLock = config.EnforceSsoOnly && !IsExempt(config.SsoOnlyExemptUsernames, user.Username) && HasSsoLink(user.Id);

        if (ownedByUs == shouldLock)
        {
            return;
        }

        var policy = userManager.GetUserDto(user).Policy;
        policy.AuthenticationProviderId = shouldLock ? typeof(SsoOnlyAuthProvider).FullName : DefaultAuthProviderId;
        await userManager.UpdatePolicyAsync(user.Id, policy).ConfigureAwait(false);

        if (shouldLock)
        {
            logger.LogInformation("Password login disabled for {Username}: the account has an SSO link and SSO-only login is enforced", user.Username);
        }
        else
        {
            logger.LogInformation("Password login restored for {Username}", user.Username);
        }
    }

    private static bool IsExempt(string[] exemptUsernames, string username)
    {
        return exemptUsernames is not null
            && exemptUsernames.Any(name => string.Equals(name?.Trim(), username, StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasSsoLink(Guid userId)
    {
        var config = SSOPlugin.Instance.Configuration;
        return config.OidConfigs.Values.Any(provider => provider.CanonicalLinks.ContainsValue(userId))
            || config.SamlConfigs.Values.Any(provider => provider.CanonicalLinks.ContainsValue(userId));
    }
}
