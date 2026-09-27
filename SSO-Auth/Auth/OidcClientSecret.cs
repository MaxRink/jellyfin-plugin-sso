using System;
using System.IO;
using Jellyfin.Plugin.SSO_Auth.Config;

namespace Jellyfin.Plugin.SSO_Auth.Auth;

/// <summary>
/// Resolves an explicitly configured file or environment reference instead of persisting a secret.
/// Inspired by aussierk's GPL-3.0 ClientSecretResolver; unresolved references fail closed.
/// </summary>
internal static class OidcClientSecret
{
    /// <summary>Reads the configured secret without falling back from a broken external reference.</summary>
    /// <param name="config">The provider configuration.</param>
    /// <returns>The client secret, or null for a public client.</returns>
    internal static string Resolve(OidConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.OidSecretFile) && !string.IsNullOrWhiteSpace(config.OidSecretEnvironmentVariable))
        {
            throw new InvalidOperationException("Configure only one external client-secret source.");
        }

        if (!string.IsNullOrWhiteSpace(config.OidSecretFile))
        {
            return RequireValue(File.ReadAllText(config.OidSecretFile.Trim()).Trim());
        }

        if (!string.IsNullOrWhiteSpace(config.OidSecretEnvironmentVariable))
        {
            return RequireValue(Environment.GetEnvironmentVariable(config.OidSecretEnvironmentVariable.Trim()));
        }

        return config.OidSecret?.Trim();
    }

    private static string RequireValue(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            throw new InvalidOperationException("The external client-secret source is empty or unavailable.");
        }

        return value;
    }
}
