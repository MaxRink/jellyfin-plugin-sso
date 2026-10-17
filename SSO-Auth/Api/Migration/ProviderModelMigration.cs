using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.SSO_Auth.Config;
using MediaBrowser.Model.Dto;
using Newtonsoft.Json;

namespace Jellyfin.Plugin.SSO_Auth.Api.Migration;

/// <summary>Previews and applies an explicit, checked migration without guessing which keys are subjects.</summary>
internal static class ProviderModelMigration
{
    internal static string Fingerprint(object provider)
    {
        var json = Newtonsoft.Json.Linq.JObject.FromObject(provider);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json.ToString(Formatting.None))));
    }

    internal static string Authority(string mode, object provider) => mode == "oid"
        ? ((OidConfig)provider).OidEndpoint?.Trim()
        : ((SamlConfig)provider).SamlEndpoint?.Trim();

    internal static (object Provider, ProviderMigration Migration, SerializableDictionary<string, Guid> Links) Get(PluginConfiguration config, string mode, string provider)
    {
        if (mode == "oid" && config.OidConfigs.TryGetValue(provider, out var oid))
        {
            return (oid, oid.Migration ??= new ProviderMigration(), oid.CanonicalLinks);
        }

        if (mode == "saml" && config.SamlConfigs.TryGetValue(provider, out var saml))
        {
            return (saml, saml.Migration ??= new ProviderMigration(), saml.CanonicalLinks);
        }

        throw new ArgumentException("Unknown provider or protocol.");
    }

    internal static void EnsureAuthority(string mode, object provider, ProviderMigration migration)
    {
        if (migration.Enabled && !string.Equals(migration.Issuer, Authority(mode, provider), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The identity authority changed after migration. Review the provider before accepting identities.");
        }
    }

    internal static MigrationPreview Preview(PluginConfiguration config, string mode, string provider, Func<Guid, UserDto> getUser)
    {
        var current = Get(config, mode, provider);
        return new MigrationPreview
        {
            Fingerprint = Fingerprint(current.Provider),
            Issuer = Authority(mode, current.Provider),
            Enabled = current.Migration.Enabled,
            Links = current.Links.Select(link => new MigrationLink
            {
                PreviousKey = link.Key,
                UserId = link.Value,
                Username = getUser(link.Value)?.Name,
                // No guess: even a key that looks like a subject must be confirmed against the IdP.
                Subject = null
            }).ToArray(),
            ManualFolders = new SerializableDictionary<string, ManualFolderGrant>(),
            CurrentFolders = new SerializableDictionary<string, ManualFolderGrant>(current.Links.Values.Distinct().ToDictionary(
                id => id.ToString("D"), id => new ManualFolderGrant
                {
                    AllFolders = getUser(id)?.Policy?.EnableAllFolders ?? false,
                    Folders = getUser(id)?.Policy?.EnabledFolders?.Select(folder => folder.ToString("D")).ToArray() ?? Array.Empty<string>()
                }))
        };
    }

    internal static PluginConfiguration Apply(PluginConfiguration config, string mode, string provider, MigrationPreview request, Func<Guid, UserDto> getUser)
    {
        var clone = JsonConvert.DeserializeObject<PluginConfiguration>(JsonConvert.SerializeObject(config));
        var current = Get(clone, mode, provider);
        if (request == null || current.Migration.Enabled
            || !string.Equals(request.Fingerprint, Fingerprint(current.Provider), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The preview is stale or this provider is already migrated.");
        }

        var authority = Authority(mode, current.Provider);
        if (string.IsNullOrWhiteSpace(authority) || !string.Equals(request.Issuer, authority, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Confirm the provider authority from a fresh preview.");
        }

        var mappings = request.Links ?? Array.Empty<MigrationLink>();
        if (mappings.Length != current.Links.Count
            || mappings.Any(link => link == null || link.PreviousKey == null)
            || mappings.Select(link => link.PreviousKey).Distinct(StringComparer.Ordinal).Count() != current.Links.Count)
        {
            throw new InvalidOperationException("Every existing link must be reviewed exactly once.");
        }

        var links = new SerializableDictionary<string, Guid>();
        foreach (var mapping in mappings)
        {
            if (string.IsNullOrWhiteSpace(mapping.Subject)
                || !current.Links.TryGetValue(mapping.PreviousKey, out var userId)
                || userId != mapping.UserId || getUser(userId) == null
                || !links.TryAdd(mapping.Subject, userId))
            {
                throw new InvalidOperationException("Confirm a unique subject and an existing account for each link. Remove stale links before migrating.");
            }
        }

        var users = links.Values.Distinct().Select(id => id.ToString("D")).ToHashSet(StringComparer.Ordinal);
        var grants = request.ManualFolders ?? new SerializableDictionary<string, ManualFolderGrant>();
        if (!users.SetEquals(grants.Keys))
        {
            throw new InvalidOperationException("Explicitly review manual folders for every linked account, including empty grants.");
        }

        foreach (var grant in grants.Values)
        {
            if (grant == null || (grant.Folders ?? Array.Empty<string>()).Any(folder => !Guid.TryParse(folder, out _)))
            {
                throw new InvalidOperationException("Manual folders must contain valid library IDs.");
            }
        }

        current.Links.Clear();
        foreach (var link in links)
        {
            current.Links.Add(link.Key, link.Value);
        }

        current.Migration.Enabled = true;
        current.Migration.PolicyApplied = false;
        current.Migration.Issuer = authority;
        current.Migration.ManualFolders = JsonConvert.DeserializeObject<SerializableDictionary<string, ManualFolderGrant>>(JsonConvert.SerializeObject(grants));
        return clone;
    }
}

/// <summary>A migration preview which must be explicitly completed by an administrator.</summary>
public class MigrationPreview
{
    /// <summary>Gets or sets the version fingerprint returned by preview.</summary>
    public string Fingerprint { get; set; }

    /// <summary>Gets or sets the authority being confirmed.</summary>
    public string Issuer { get; set; }

    /// <summary>Gets or sets a value indicating whether migration is already enabled.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets the complete reviewed link list.</summary>
    public MigrationLink[] Links { get; set; }

    /// <summary>Gets or sets current effective library access for review only; never copied into manual grants automatically.</summary>
    public SerializableDictionary<string, ManualFolderGrant> CurrentFolders { get; set; }

    /// <summary>Gets or sets explicit manual folder grants, keyed by Jellyfin account ID.</summary>
    public SerializableDictionary<string, ManualFolderGrant> ManualFolders { get; set; }
}

/// <summary>An explicit confirmation that an existing local account belongs to an IdP subject.</summary>
public class MigrationLink
{
    /// <summary>Gets or sets the existing canonical-link key.</summary>
    public string PreviousKey { get; set; }

    /// <summary>Gets or sets the account ID that must remain unchanged.</summary>
    public Guid UserId { get; set; }

    /// <summary>Gets or sets the local username, shown for review and never used for identity matching.</summary>
    public string Username { get; set; }

    /// <summary>Gets or sets the confirmed IdP subject. Must be provided; no username inference occurs.</summary>
    public string Subject { get; set; }
}
