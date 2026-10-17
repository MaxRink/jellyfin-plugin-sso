using System;

namespace Jellyfin.Plugin.SSO_Auth.Config;

/// <summary>Manually maintained folder access, separate from a provider's role grants.</summary>
public class ManualFolderGrant
{
    /// <summary>Gets or sets a value indicating whether all libraries are manually granted.</summary>
    public bool AllFolders { get; set; }

    /// <summary>Gets or sets the explicitly granted library IDs.</summary>
    public string[] Folders { get; set; } = Array.Empty<string>();
}

/// <summary>Settings for the opt-in explicit identity and folder model.</summary>
public class ProviderMigration
{
    /// <summary>Gets or sets a value indicating whether identities use only reviewed subject keys.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets the identity authority pinned when the migration was reviewed.</summary>
    public string Issuer { get; set; }

    /// <summary>Gets or sets explicit manual library grants keyed by Jellyfin user ID.</summary>
    public SerializableDictionary<string, ManualFolderGrant> ManualFolders { get; set; } = new();

    /// <summary>Gets or sets a value indicating whether a migrated login has begun applying user policy; prevents configuration-only rollback after use.</summary>
    public bool PolicyApplied { get; set; }
}
