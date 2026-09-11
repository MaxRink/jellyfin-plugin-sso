using System;
using System.Collections.Generic;
using System.Xml.Serialization;

namespace Jellyfin.Plugin.SSO_Auth.Config;

/// <summary>
/// Plugin Configuration.
/// </summary>
public class PluginConfiguration : MediaBrowser.Model.Plugins.BasePluginConfiguration
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PluginConfiguration"/> class.
    /// </summary>
    public PluginConfiguration()
    {
        SamlConfigs = new SerializableDictionary<string, SamlConfig>();
        OidConfigs = new SerializableDictionary<string, OidConfig>();
        SsoOnlyExemptUsernames = Array.Empty<string>();
    }

    /// <summary>
    /// Gets or sets the SAML configurations available.
    /// </summary>
    [XmlElement("SamlConfigs")]
    public SerializableDictionary<string, SamlConfig> SamlConfigs { get; set; }

    /// <summary>
    /// Gets or sets the OpenID configurations available.
    /// </summary>
    [XmlElement("OidConfigs")]
    public SerializableDictionary<string, OidConfig> OidConfigs { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether accounts that have an SSO link may only sign in
    /// through SSO. Such accounts are moved to an authentication provider that always rejects, so
    /// their password stops working. Accounts without any SSO link are never touched, which keeps a
    /// break-glass administrator able to sign in.
    /// </summary>
    public bool EnforceSsoOnly { get; set; }

    /// <summary>
    /// Gets or sets the usernames that keep password login even while <see cref="EnforceSsoOnly"/>
    /// is set. Matched case-insensitively against the Jellyfin username.
    /// </summary>
    public string[] SsoOnlyExemptUsernames { get; set; }
}

/// <summary>
/// The configuration required for a SAML flow.
/// </summary>
[XmlRoot("PluginConfiguration")]
public class SamlConfig
{
    private SerializableDictionary<string, Guid> _canonicalLinks;
    private SerializableDictionary<string, string> _usernameMappings;

    /// <summary>
    /// Gets or sets the SAML information endpoint.
    /// </summary>
    public string SamlEndpoint { get; set; }

    /// <summary>
    /// Gets or sets the SAML provider's client ID.
    /// </summary>
    public string SamlClientId { get; set; }

    /// <summary>
    /// Gets or sets the SAML public key.
    /// </summary>
    public string SamlCertificate { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the provider is enabled.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether RBAC is enabled.
    /// </summary>
    public bool EnableAuthorization { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether all folders are allowed by default.
    /// </summary>
    public bool EnableAllFolders { get; set; }

    /// <summary>
    /// Gets or sets what folders should users have access to by default.
    /// </summary>
    public string[] EnabledFolders { get; set; }

    /// <summary>
    /// Gets or sets the roles that are checked to determine whether the user is an administrator.
    /// </summary>
    public string[] AdminRoles { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether existing admin permissions are preserved when no
    /// admin role matches. When false (default), admin status is synced strictly from roles.
    /// </summary>
    public bool PreserveAdminPermissions { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a first login for an unlinked identity may adopt
    /// an existing local Jellyfin account of the same username. When true, such logins are
    /// refused until the account is linked from the self-service page.
    /// </summary>
    public bool DisableUsernameAccountAdoption { get; set; }

    /// <summary>
    /// Gets or sets explicit provider-username to Jellyfin-username mappings, applied before an
    /// account is looked up or created. Keys are matched case-insensitively (the comparer of a
    /// deserialised dictionary is not preserved, so the lookup does the comparison itself).
    /// </summary>
    public SerializableDictionary<string, string> UsernameMappings
    {
        get => _usernameMappings ??= new SerializableDictionary<string, string>();
        set => _usernameMappings = value;
    }

    /// <summary>
    /// Gets or sets the URL that ends the session at the identity provider. Leave empty to use the
    /// provider's advertised <c>end_session_endpoint</c>. Providers without one (Authelia, for
    /// example) need their own logout URL here, such as
    /// <c>https://auth.example.com/logout</c>.
    /// </summary>
    public string LogoutUrl { get; set; }

    /// <summary>
    /// Gets or sets what roles are checked to determine whether the user is allowed to use Jellyfin.
    /// </summary>
    public string[] Roles { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether RBAC is used to manage folder access.
    /// </summary>
    public bool EnableFolderRoles { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether RBAC is used to manage Live TV access.
    /// </summary>
    public bool EnableLiveTvRoles { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether Live TV is enabled by default.
    /// </summary>
    public bool EnableLiveTv { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether Live TV is allowed to be managed by default.
    /// </summary>
    public bool EnableLiveTvManagement { get; set; }

    /// <summary>
    /// Gets or sets the roles that are checked to determine whether the user is allowed to view Live TV.
    /// </summary>
    public string[] LiveTvRoles { get; set; }

    /// <summary>
    /// Gets or sets the roles that are checked to determine whether the user is allowed to manage Live TV.
    /// </summary>
    public string[] LiveTvManagementRoles { get; set; }

    /// <summary>
    /// Gets or sets which folders map to what roles in RBAC.
    /// </summary>
    [XmlArray("FolderRoleMappings")]
    [XmlArrayItem(typeof(FolderRoleMap), ElementName = "FolderRoleMappings")]
    public List<FolderRoleMap> FolderRoleMapping { get; set; }

    /// <summary>
    /// Gets or sets the default provider the user after logging in with SSO.
    /// </summary>
    public string DefaultProvider { get; set; }

    /// <summary>
    /// Gets or sets the redirect scheme override.
    /// </summary>
    public string SchemeOverride { get; set; }

    /// <summary>
    /// Gets or sets the redirect port override.
    /// </summary>
    public int? PortOverride { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the new, more descriptive paths are to be used.
    /// </summary>
    public bool NewPath { get; set; }

    /// <summary>
    /// Gets or sets a mapping of canonical names from the provider to jellyfin user ids.
    /// </summary>
    [XmlElement("CanonicalLinks")]
    public SerializableDictionary<string, Guid> CanonicalLinks
    {
        // Assigned rather than returned: handing out a throwaway dictionary would silently drop
        // whatever the caller writes into it.
        get => _canonicalLinks ??= new SerializableDictionary<string, Guid>();
        set => _canonicalLinks = value;
    }
}

/// <summary>
/// The configuration required for a OpenID flow.
/// </summary>
[XmlRoot("PluginConfiguration")]
public class OidConfig
{
    private SerializableDictionary<string, Guid> _canonicalLinks;
    private SerializableDictionary<string, string> _usernameMappings;

    /// <summary>
    /// Gets or sets the OpenID well-known information endpoint.
    /// </summary>
    public string OidEndpoint { get; set; }

    /// <summary>
    /// Gets or sets OpenID client ID.
    /// </summary>
    public string OidClientId { get; set; }

    /// <summary>
    /// Gets or sets OpenID shared secret.
    /// </summary>
    public string OidSecret { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the provider is enabled.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether RBAC is enabled.
    /// </summary>
    public bool EnableAuthorization { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether all folders are allowed by default.
    /// </summary>
    public bool EnableAllFolders { get; set; }

    /// <summary>
    /// Gets or sets what folders should users have access to by default.
    /// </summary>
    public string[] EnabledFolders { get; set; }

    /// <summary>
    /// Gets or sets the roles that are checked to determine whether the user is an administrator.
    /// </summary>
    public string[] AdminRoles { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether existing admin permissions are preserved when no
    /// admin role matches. When false (default), admin status is synced strictly from roles.
    /// </summary>
    public bool PreserveAdminPermissions { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a first login for an unlinked identity may adopt
    /// an existing local Jellyfin account of the same username. When true, such logins are
    /// refused until the account is linked from the self-service page.
    /// </summary>
    public bool DisableUsernameAccountAdoption { get; set; }

    /// <summary>
    /// Gets or sets explicit provider-username to Jellyfin-username mappings, applied before an
    /// account is looked up or created. Keys are matched case-insensitively (the comparer of a
    /// deserialised dictionary is not preserved, so the lookup does the comparison itself).
    /// </summary>
    public SerializableDictionary<string, string> UsernameMappings
    {
        get => _usernameMappings ??= new SerializableDictionary<string, string>();
        set => _usernameMappings = value;
    }

    /// <summary>
    /// Gets or sets the URL that ends the session at the identity provider. Leave empty to use the
    /// provider's advertised <c>end_session_endpoint</c>. Providers without one (Authelia, for
    /// example) need their own logout URL here, such as
    /// <c>https://auth.example.com/logout</c>.
    /// </summary>
    public string LogoutUrl { get; set; }

    /// <summary>
    /// Gets or sets what roles are checked to determine whether the user is allowed to use Jellyfin.
    /// </summary>
    public string[] Roles { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether RBAC is used to manage folder access.
    /// </summary>
    public bool EnableFolderRoles { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether RBAC is used to manage Live TV access.
    /// </summary>
    public bool EnableLiveTvRoles { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether Live TV is enabled by default.
    /// </summary>
    public bool EnableLiveTv { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether Live TV is allowed to be managed by default.
    /// </summary>
    public bool EnableLiveTvManagement { get; set; }

    /// <summary>
    /// Gets or sets the roles that are checked to determine whether the user is allowed to view Live TV.
    /// </summary>
    public string[] LiveTvRoles { get; set; }

    /// <summary>
    /// Gets or sets the roles that are checked to determine whether the user is allowed to manage Live TV.
    /// </summary>
    public string[] LiveTvManagementRoles { get; set; }

    /// <summary>
    /// Gets or sets which folders map to what roles in RBAC.
    /// </summary>
    [XmlArray("FolderRoleMappings")]
    [XmlArrayItem(typeof(FolderRoleMap), ElementName = "FolderRoleMappings")]
    public List<FolderRoleMap> FolderRoleMapping { get; set; }

    /// <summary>
    /// Gets or sets the claim paths to check roles against. Separated by spaces.
    /// </summary>
    public string RoleClaim { get; set; }

    /// <summary>
    /// Gets or Sets additional Scopes to request access to in the authorization request.
    /// </summary>
    public string[] OidScopes { get; set; }

    /// <summary>
    /// Gets or sets the default provider the user after logging in with SSO.
    /// </summary>
    public string DefaultProvider { get; set; }

    /// <summary>
    /// Gets or sets the redirect scheme override.
    /// </summary>
    public string SchemeOverride { get; set; }

    /// <summary>
    /// Gets or sets the redirect port override.
    /// </summary>
    public int? PortOverride { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the new, more descriptive paths are to be used.
    /// </summary>
    public bool NewPath { get; set; }

    /// <summary>
    /// Gets or sets a mapping of canonical names from the provider to jellyfin user ids.
    /// </summary>
    [XmlElement("CanonicalLinks")]
    public SerializableDictionary<string, Guid> CanonicalLinks
    {
        // Assigned rather than returned: handing out a throwaway dictionary would silently drop
        // whatever the caller writes into it.
        get => _canonicalLinks ??= new SerializableDictionary<string, Guid>();
        set => _canonicalLinks = value;
    }

    /// <summary>
    /// Gets or sets the default username claim when creating new accounts.
    /// </summary>
    public string DefaultUsernameClaim { get; set; }

    /// <summary>
    /// Gets or sets the URL format of the new user avatar.
    /// </summary>
    public string AvatarUrlFormat { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether HTTPS in the discovery endpoint is required.
    /// </summary>
    public bool DisableHttps { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether pushed authorization is required.
    /// </summary>
    public bool DisablePushedAuthorization { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether client credentials are sent as an HTTP Basic
    /// authorization header instead of in the request body. Pushed Authorization Requests always
    /// use the header, so a provider configured for Basic (Authelia's
    /// <c>token_endpoint_auth_method: client_secret_basic</c>) needs this to keep the token
    /// request consistent with it.
    /// </summary>
    public bool UseClientSecretBasic { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the OpenID endpoints are validated.
    /// </summary>
    public bool DoNotValidateEndpoints { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the OpenID issuer name is validated.
    /// </summary>
    public bool DoNotValidateIssuerName { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the UserInfo endpoint is used to get profile data.
    /// </summary>
    public bool DoNotLoadProfile { get; set; }
}

/// <summary>
/// The OpenID client ID.
/// </summary>
public class FolderRoleMap
{
    /// <summary>
    /// Gets or sets the role of the mapping.
    /// </summary>
    public string Role { get; set; }

    /// <summary>
    /// Gets or sets the folders that are allowed from the given role.
    /// </summary>
    public List<string> Folders { get; set; }
}
