<h1 align="center">Jellyfin SSO Plugin</h1>

<p align="center">

<img alt="Logo" src="https://raw.githubusercontent.com/9p4/jellyfin-plugin-sso/main/img/logo.png"/>
<br/>
<br/>
<a href="https://github.com/MaxRink/jellyfin-plugin-sso">
<img alt="GPL 3.0 License" src="https://img.shields.io/github/license/MaxRink/jellyfin-plugin-sso.svg"/>
</a>
<a href="https://github.com/MaxRink/jellyfin-plugin-sso/actions/workflows/dotnet.yml">
<img alt="GitHub Actions Build Status" src="https://github.com/MaxRink/jellyfin-plugin-sso/actions/workflows/dotnet.yml/badge.svg"/>
</a>
<a href="https://github.com/MaxRink/jellyfin-plugin-sso/releases">
<img alt="Current Release" src="https://img.shields.io/github/release/MaxRink/jellyfin-plugin-sso.svg"/>
</a>
<a href="https://github.com/MaxRink/jellyfin-plugin-sso/releases.atom">
<img alt="Release RSS Feed" src="https://img.shields.io/badge/rss-releases-ffa500?logo=rss" />
</a>
<a href="https://github.com/MaxRink/jellyfin-plugin-sso/commits/main.atom">
<img alt="Main Commits RSS Feed" src="https://img.shields.io/badge/rss-commits-ffa500?logo=rss" />
</a>
</p>

> **Note:** The original project at [9p4/jellyfin-plugin-sso](https://github.com/9p4/jellyfin-plugin-sso) has been archived by its author. This repository ([MaxRink/jellyfin-plugin-sso](https://github.com/MaxRink/jellyfin-plugin-sso)) is a maintained fork that targets **Jellyfin 12** and consolidates fixes and features from the wider fork ecosystem (see [Acknowledgements](#acknowledgements)). Install it using **our** plugin manifest (see [Installing](#installing) below).

This plugin allows users to sign in through an SSO provider (such as Google, Microsoft, or your own provider). This enables one-click signin.

https://user-images.githubusercontent.com/17993169/149681516-f93b43f5-fa5c-4c1f-a909-e5414878a864.mp4

Existing users may link new SSO accounts, or remove existing links using self-service at `/SSOViews/linking`. Since 6.0.0.0 a linking flow can only be started from that page while signed in (the old unauthenticated `?isLinking=true` start is refused), and OpenID links are keyed by the provider's stable `sub` claim instead of the username; links from earlier releases are migrated on the next login.

## Current State:

This is 100% alpha software! PRs are welcome to improve the code.

~~There is NO admin configuration! You must use the API to configure the program!~~ Added by [strazto](https://github.com/strazto) in PR [#18](https://github.com/9p4/jellyfin-plugin-sso/pull/18) and [#27](https://github.com/9p4/jellyfin-plugin-sso/pull/27).

**[This is for Jellyfin >=12.0](https://github.com/9p4/jellyfin-plugin-sso/issues/3).** Version 6.1.0.0 targets **Jellyfin 12** (`net10.0`, plugin ABI `12.0.0.0`), built against `Jellyfin.Controller`/`Jellyfin.Model` `12.0.0`. For Jellyfin 10.11 use the 4.x/5.x releases. As of 5.0.0.0 the SSO login flow works in the web UI **and** in the native Jellyfin mobile apps (Android, and the Expo-based iOS app). Clients where the in-app browser flow is unavailable (e.g. some TVs, Swiftfin) can still log in via [Quick Connect](https://jellyfin.org/docs/general/server/quick-connect).

**This README reflects the branch it is currently on! Switch tags to view version-specific documentation!**

## Tested Providers

[Find provider specific documentation in providers.md](providers.md)

- Authelia
- authentik
- Keycloak
  - OIDC & SAML
- Pocket ID
- Kanidm
- Google OpenID: Works, but usernames are all numeric

## Supported Protocols

- [OpenID](https://openid.net/developers/how-connect-works/)
- [SAML](https://www.cloudflare.com/learning/access-management/what-is-saml/)

## Security

This is my first time writing C# so please take all of the code written here with a grain of salt. This program should be reasonably secure since it validates all information passed from the client with either a certificate or a secret internal state.

## Installing

Add this fork's package repo [https://raw.githubusercontent.com/MaxRink/jellyfin-plugin-sso/manifest-release/manifest.json](https://raw.githubusercontent.com/MaxRink/jellyfin-plugin-sso/manifest-release/manifest.json) to your Jellyfin plugin repositories.

Then, install the plugin from the plugin catalog!

> The original `9p4/jellyfin-plugin-sso` manifest is archived and no longer updated. Use the `MaxRink` manifest above to receive current releases. If you previously added the old `9p4` repository, replace it with this one.

See [Contributing](#contributing) for instructions on how to build from source.

### (Fallback) Legacy package repo (Versions <= 3.3.0)

We have transitioned to a release system that automates distribution, packaging & hosting.
This system is new, and if something goes wrong, you can try using the old package repository as a fallback.

Instead add the **old** package repository: [https://repo.ersei.net/jellyfin/manifest.json](https://repo.ersei.net/jellyfin/manifest.json) to your jellyfin plugin repositories.

### Installing cutting edge/nightly builds

If you're impatient/brave/feel like helping us test things out, you can install the nightly build of the plugin, which is automatically built against the main branch.

The nightly build can be installed from the [main plugin repo](https://raw.githubusercontent.com/MaxRink/jellyfin-plugin-sso/manifest-release/manifest.json), and will always have a version number of `0.0.0.9000`.

The nightly build may have new features unavailable in other builds, but **be warned**, things may change frequently in nightly builds, and things may break, and you could lose data.

## Roadmap

- [x] Admin page
- [ ] Automated tests
- [x] Add role/claims support
- [x] Use canonical usernames instead of preferred usernames
- [x] Add user self-service
- [ ] Finalize RBAC access for all user properties

## Examples

### Creating A Login Button On The Main Page

In the Jellyfin administration UI, under "General", there is a "Branding" section. In that section, add the following code in the "Login disclaimer" block (replacing `PROVIDER_NAME` and the domain):

```html
<form action="https://jellyfin.example.com/sso/OID/start/PROVIDER_NAME">
  <button class="raised block emby-button button-submit">
    Sign in with SSO
  </button>
</form>
```

Then, add the following code in the "Custom CSS code" section:

```css
a.raised.emby-button {
  padding: 0.9em 1em;
  color: inherit !important;
}

.disclaimerContainer {
  display: block;
}
```

![screenshot of the configuration page with the same code](img/custom-button.png)

For more information, refer to [issue #16](https://github.com/9p4/jellyfin-plugin-sso/issues/16).

### Sending Users To A Page After Login

Add `returnUrl` to the start URL and the browser opens that page once the login
finishes, for example
`/sso/OID/start/PROVIDER_NAME?returnUrl=/movies`. The value must be a path on
this server; anything that could point somewhere else is ignored and the user
lands on the home page instead. `url` works as well, because that is the name
the Jellyfin web client uses when it remembers where someone was going.

A Quick Connect code takes priority: if the login carries `?qc=CODE`, the user
goes to the Quick Connect page regardless of `returnUrl`.

### Signing Out Of The Provider As Well

Signing out of Jellyfin ends the Jellyfin session only. The provider still has
its own session, so the next click on the SSO button signs the user straight
back in. To end both, send the user to:

```
/SSOViews/logout?provider=PROVIDER_NAME
```

That page ends the Jellyfin session first, then forwards the browser to the
provider. Where it forwards to is decided in this order:

1. the **Logout URL** set for the provider, if there is one,
2. the `end_session_endpoint` from the provider's discovery document,
3. the Jellyfin login page, if the provider offers neither.

Most providers publish `end_session_endpoint`, so the Logout URL can stay empty.
**Authelia does not publish one**, so set it to `https://auth.example.com/logout`
there. The request carries `client_id`, plus the Jellyfin login page as both
`post_logout_redirect_uri` and `rd`, because providers disagree on which one
they read. Some providers only accept a post-logout redirect that is registered
with them, so register the Jellyfin login page if the user ends up on an error
page.

You can add the link the same way as the login button, in the **Branding**
settings:

```html
<a
  class="raised block emby-button"
  href="https://jellyfin.example.com/SSOViews/logout?provider=PROVIDER_NAME"
>
  Sign out of SSO
</a>
```

### Requiring SSO For Linked Accounts

**SSO-Only Login** in the admin page turns off password login for every account
that has an SSO link. Those accounts are moved to an authentication provider
that rejects every password, so they can only get in through SSO.

Two things keep this from locking everyone out:

- Accounts **without** an SSO link are never touched. A local administrator who
  has never used SSO keeps their password, so there is always a way back in if
  the provider is unreachable.
- The exemption list keeps named accounts on password login even when they do
  have an SSO link.

Switching the setting off restores password login for the accounts it locked.
An account is also evaluated when it is created and on every SSO login.

If an account does get locked out, an API key still works:

```bash
curl -X POST -H "Content-Type: application/json" \
  -d '"Jellyfin.Server.Implementations.Users.DefaultAuthenticationProvider"' \
  "https://jellyfin.example.com/sso/Unregister/USERNAME?api_key=API_KEY_HERE"
```

### SAML

Example for adding a SAML configuration with the API using [curl](https://curl.se/):

`curl -v -X POST -H "Content-Type: application/json" -d '{"samlEndpoint": "https://keycloak.example.com/realms/test/protocol/saml", "samlClientId": "jellyfin-saml", "samlCertificate": "Very long base64 encoded string here", "enabled": true, "enableAuthorization": true, "enableAllFolders": false, "enabledFolders": [], "adminRoles": ["jellyfin-admin"], "roles": ["allowed-to-use-jellyfin"], "enableFolderRoles": true, "folderRoleMapping": [{"role": "allowed-to-watch-movies", "folders": ["cc7df17e2f3509a4b5fc1d1ff0a6c4d0", "f137a2dd21bbc1b99aa5c0f6bf02a805"]}]}' "https://myjellyfin.example.com/sso/SAML/Add/PROVIDER_NAME?api_key=API_KEY_HERE"`

Make sure that the JSON is the same as the configuration you would like.

The SAML provider must have the following configuration (I am using Keycloak, and I cannot speak for whatever you will see):

- Sign Documents on
- Sign Assertions off
- Client Signature Required off
- Redirect URI: [https://myjellyfin.example.com/sso/SAML/post/PROVIDER_NAME](https://myjellyfin.example.com/sso/SAML/start/PROVIDER_NAME)
- Base URL: [https://myjellyfin.example.com](https://myjellyfin.example.com)
- Master SAML processing URL: [https://myjellyfin.example.com/sso/SAML/start/PROVIDER_NAME](https://myjellyfin.example.com/sso/SAML/start/PROVIDER_NAME)

Make sure that `clientid` is replaced with the actual client ID and `PROVIDER_NAME` is replaced with the chosen provider name!

### OpenID

Example for adding an OpenID configuration with the API using [curl](https://curl.se/)

`curl -v -X POST -H "Content-Type: application/json" -d '{"oidEndpoint": "https://keycloak.example.com/realms/test", "oidClientId": "jellyfin-oid", "oidSecret": "short secret here", "enabled": true, "enableAuthorization": true, "enableAllFolders": false, "enabledFolders": [], "adminRoles": ["jellyfin-admin"], "roles": ["allowed-to-use-jellyfin"], "enableFolderRoles": true, "folderRoleMapping": [{"role": "allowed-to-watch-movies", "folders": ["cc7df17e2f3509a4b5fc1d1ff0a6c4d0", "f137a2dd21bbc1b99aa5c0f6bf02a805"]}], "roleClaim": "realm_access", "oidScopes" : [""]}' "https://myjellyfin.example.com/sso/OID/Add/PROVIDER_NAME?api_key=API_KEY_HERE"`

The OpenID provider must have the following configuration (again, I am using Keycloak)

- Access Type: Confidential
- Standard Flow Enabled
- Redirect URI: [https://myjellyfin.example.com/sso/OID/redirect/PROVIDER_NAME](https://myjellyfin.example.com/sso/OID/redirect/PROVIDER_NAME)
- Base URL: [https://myjellyfin.example.com](https://myjellyfin.example.com)

Make sure that `clientid` is replaced with the actual client ID and `PROVIDER_NAME` is replaced with the chosen provider name!

## API Endpoints

The API is all done from a base URL of `/sso/`

### SAML

#### Flow

- POST `SAML/start/PROVIDER_NAME`: This is the SAML POST endpoint. It accepts a form response from the SAML provider and returns HTML and JavaScript for the client to login with a given provider name.
- GET `SAML/start/PROVIDER_NAME`: This is the SAML initiator: it will begin the authorization flow for SAML with a given provider name.
- POST `SAML/StartLink/PROVIDER_NAME`: Starts an account-linking flow for the signed-in Jellyfin user (requires authorization) and returns the provider URL to navigate to. The SAML response is bound to the request ID and consumer URL of that flow; a response for any other request is rejected.
- POST `SAML/Auth/PROVIDER_NAME`: This is the SAML client-side API: the HTML and JavaScript client will call this endpoint to receive Jellyfin credentials given a provider name. Post format is in JSON with the following keys:
  - `deviceId`: string. Device ID.
  - `deviceName`: string. Device name.
  - `appName`: string. App name.
  - `appVersion`: string. App version.
  - `data`: string. The signed SAML XML request. Used to verify a request.

#### Configuration

These all require authorization. Append an API key to the end of the request: `curl "http://myjellyfin.example.com/sso/SAML/Get?api_key=API_KEY_HERE"`

- POST `SAML/Add/PROVIDER_NAME`: This adds or overwrites a configuration for SAML for the given provider name. It accepts JSON with the following keys and format:
  - `samlEndpoint`: string. The SAML endpoint.
  - `samlClientId`: string. The SAML client ID.
  - `samlCertificate`: string. The base64 encoded SAML certificate.
  - `enabled`: boolean. Determines if the provider is enabled or not.
  - `enableAuthorization`: boolean: Determines if the plugin sets permissions for the user. If false, the user will start with no permissions and an administrator will add permissions. If disabled, then the permissions of users will not be modified and the Jellyfin defaults will be used instead.
  - `enableAllFolders`: boolean. Determines if the client logging in is allowed access to all folders.
  - `enabledFolders`: array of strings. If `enableAllFolders` is set to false, then this will be used to determine what folders the users who log in through this provider are allowed to use. These folders are always granted, in addition to any granted through `folderRoleMapping`.
  - `roles`: array of strings. This validates the SAML response against the `Role` attribute. If a user has any of these roles, then the user is authenticated. Leave blank to disable role checking.
  - `adminRoles`: array of strings. This uses SAML response's `Role` attributes. If a user has any of these roles, then the user is an admin. Leave blank to disable (default is to not enable admin permissions).
  - `preserveAdminPermissions`: boolean. When true, the plugin will only ever elevate users to administrator based on roles and will never revoke the administrator flag from an account that already has it. Defaults to `false`: admin status is synced strictly from the SAML response on every login.
  - `enableFolderRoles`: boolean. Determines if role-based folder access should be used.
  - `folderRoleMapping`: object in the format "role": string and "folders": array of strings. The user with this role will have access to the following folders if `enableFolderRoles` is enabled. To get the IDs of the folders, GET the `/Library/MediaFolders` URL with an API key. Look for the `Id` attribute.
  - `enableLiveTvRoles`: boolean. Determines if role-based Live TV access should be used.
  - `liveTvRoles`: array of strings. If `enableLiveTvRoles` is enabled, then the user's roles will be checked against these. If the user is granted permission, then the user will be able to view Live TV.
  - `liveTvManagementRoles`: array of strings. If `enableLiveTvRoles` is enabled, then the user's roles will be checked against these. If the user is granted permission, then the user will be able to manage Live TV.
  - `enableLiveTv`: boolean. Whether to allow Live TV by default. This applies even if `enableLiveTvRoles` is enabled.
  - `enableLiveTvManagement`: boolean. Whether to allow Live TV management by default. This applies even if `enableLiveTvRoles` is enabled.
  - `defaultProvider`: string. The set provider then gets assigned to the user after they have logged in. If it is not set, nothing is changed. With this, a user can login with SSO but is still able to log in via other providers later. See the `Unregister` endpoint. Only accounts created by this plugin are reassigned; pre-existing local or LDAP accounts keep their provider.
  - `disableUsernameAccountAdoption`: boolean. By default the first login of an identity that has no link yet takes over the local Jellyfin account with the same username, administrators included. When true, such logins are refused (HTTP 409) until the account is linked from `/SSOViews/linking`. Existing links are unaffected. Defaults to `false`.
  - `schemeOverride`: string. Sets the scheme for URLs used. Can be useful if the plugin refuses to use HTTPS URLs.
  - `usernameMappings`: object of strings. Maps a username from the provider to a different Jellyfin username. See the OpenID description above.
  - `logoutUrl`: string. Where to send the browser to end the session at the provider.
- GET `SAML/Del/PROVIDER_NAME`: This removes a configuration for SAML for a given provider name.
- GET `SAML/Get`: Lists the configurations currently available.

### OpenID

#### Flow

- GET `OID/redirect/PROVIDER_NAME`: This is the OpenID callback path. This will return HTML and JavaScript for the client to login with a given provider name.
- GET `OID/start/PROVIDER_NAME`: This is the OpenID initiator: it will begin the authorization flow for OpenID with a given provider name.
- POST `OID/StartLink/PROVIDER_NAME`: Starts an account-linking flow for the signed-in Jellyfin user (requires authorization) and returns the provider URL to navigate to. States are single-use, bound to the provider that issued them and expire after 10 minutes.
- POST `OID/Auth/PROVIDER_NAME`: This is the OpenID client-side API: the HTML and JavaScript client will call this endpoint to receive Jellyfin credentials for a given provider name. Post format is in JSON with the following keys:
  - `deviceId`: string. Device ID.
  - `deviceName`: string. Device name.
  - `appName`: string. App name.
  - `appVersion`: string. App version.
  - `data`: string. The OpenID state. Used to verify a request.
- POST `OID/DeviceAuth/PROVIDER_NAME`: Device-code / headless login endpoint ([RFC 8628](https://datatracker.ietf.org/doc/html/rfc8628)). A client that has completed the OAuth2 device authorization grant with the provider posts the resulting `id_token`; the plugin validates the JWT against the provider's JWKS and exchanges it for a Jellyfin session, applying the same role/folder/Live TV RBAC as the redirect flow. Useful for TVs and other clients without an in-app browser. Post format is JSON:
  - `idToken`: string. The OIDC `id_token` obtained from the device authorization grant.
  - `deviceId`, `deviceName`, `appName`, `appVersion`: string. Client identification, as above.
- GET `OID/logout/PROVIDER_NAME`: Redirects the browser to the provider's logout URL so the session there ends too. Usually reached through `/SSOViews/logout?provider=PROVIDER_NAME`, which ends the Jellyfin session first. No authorization needed, because the browser has already dropped its token by then, and the endpoint only redirects to a URL from the provider configuration.
- Quick Connect: append `?qc=CODE` to `OID/start/PROVIDER_NAME` to carry a Jellyfin [Quick Connect](https://jellyfin.org/docs/general/server/quick-connect) code through the login; after authentication the user is redirected to the Quick Connect confirmation page with the code prefilled.

#### Configuration

These all require authorization. Append an API key to the end of the request: `curl "http://myjellyfin.example.com/sso/OID/Get?api_key=9c6e5fae4ae145669e6b7a3942f813b7"`

- POST `OID/Add/PROVIDERNAME`: This adds or overwrites a configuration for OpenID with a given provider name. It accepts JSON with the following keys and format:
  - `oidEndpoint`: string. The OpenID endpoint. Must have a `.well-known` path available.
  - `oidClientId`: string. The OpenID client ID.
  - `oidSecret`: string. The OpenID secret.
  - `enabled`: boolean. Determines if the provider is enabled or not.
  - `enableAuthorization`: boolean: Determines if the plugin sets permissions for the user. If false, the user will start with no permissions and an administrator will add permissions. If disabled, then the permissions of users will not be modified and the Jellyfin defaults will be used instead.
  - `enableAllFolders`: boolean. Determines if the client logging in is allowed access to all folders.
  - `enabledFolders`: array of strings. If `enableAllFolders` is set to false, then this will be used to determine what folders the users who log in through this provider are allowed to use. These folders are always granted, in addition to any granted through `folderRoleMapping`.
  - `roles`: array of strings. This validates the OpenID response against the claim set in `roleClaim`. If a user has any of these roles, then the user is authenticated. Leave blank to disable role checking. This currently only works for Keycloak (to my knowledge).
  - `adminRoles`: array of strings. This uses the OpenID response against the claim set in `roleClaim`. If a user has any of these roles, then the user is an admin. Leave blank to disable (default is to not enable admin permissions).
  - `preserveAdminPermissions`: boolean. When true, the plugin will only ever elevate users to administrator based on roles and will never revoke the administrator flag from an account that already has it. Defaults to `false`: admin status is synced strictly from the OIDC response on every login.
  - `enableFolderRoles`: boolean. Determines if role-based folder access should be used.
  - `folderRoleMapping`: object in the format "role": string and "folders": array of strings. The user with this role will have access to the following folders if `enableFolderRoles` is enabled. To get the IDs of the folders, GET the `/Library/MediaFolders` URL with an API key. Look for the `Id` attribute.
  - `enableLiveTvRoles`: boolean. Determines if role-based Live TV access should be used.
  - `liveTvRoles`: array of strings. If `enableLiveTvRoles` is enabled, then the user's roles will be checked against these. If the user is granted permission, then the user will be able to view Live TV.
  - `liveTvManagementRoles`: array of strings. If `enableLiveTvRoles` is enabled, then the user's roles will be checked against these. If the user is granted permission, then the user will be able to manage Live TV.
  - `enableLiveTv`: boolean. Whether to allow Live TV by default. This applies even if `enableLiveTvRoles` is enabled.
  - `enableLiveTvManagement`: boolean. Whether to allow Live TV management by default. This applies even if `enableLiveTvRoles` is enabled.
  - `roleClaim`: string. This is the value in the OpenID response to check for roles. For Keycloak, it is `realm_access.roles` by default. The first element is the claim type, the subsequent values are to parse the JSON of the claim value. Use a "\\." to denote a literal ".". Several claim paths may be given separated by spaces (for example `realm_access.roles groups`); roles from all of them are combined. The value may be a list of strings, or a JSON object whose keys are the role names (as Zitadel's `urn:zitadel:iam:org:project:roles` claim).
  - `oidScopes` : array of strings. Each contains an additional scope name to include in the OIDC request.
    - For some OIDC providers (For example, [authelia](https://github.com/9p4/jellyfin-plugin-sso/issues/23#issuecomment-1112237616)), additional scopes may be required in order to validate group membership in role claims.
    - Leave empty to only request the default scopes.
  - `defaultProvider`: string. The set provider then gets assigned to the user after they have logged in. If it is not set, nothing is changed. With this, a user can login with SSO but is still able to log in via other providers later. See the `Unregister` endpoint. Only accounts created by this plugin are reassigned; pre-existing local or LDAP accounts keep their provider.
  - `disableUsernameAccountAdoption`: boolean. By default the first login of an identity that has no link yet takes over the local Jellyfin account with the same username, administrators included. When true, such logins are refused (HTTP 409) until the account is linked from `/SSOViews/linking`. Existing links are unaffected. Defaults to `false`.
  - `defaultUsernameClaim`: string. The provider will use the claim to create the users' usernames. If not set, it fallbacks to `preferred_username`.
  - `avatarUrlFormat`: string. The URL format for the users avatars. OIDC claims can be used by using the `@{claim_type}` syntax. A claim containing an inline `data:image/...;base64,` picture (Kanidm, Pocket ID) is accepted as well. If not set, the avatars won't change.
  - `doNotLoadProfile`: boolean. Skips the OIDC UserInfo request and relies on the claims in the ID token alone. Required for providers whose UserInfo endpoint is unusable, such as Cloudflare Access (see [providers.md](providers.md)).
  - `usernameMappings`: object of strings. Maps a username from the provider to a different Jellyfin username, for example `{"max.mustermann": "Max"}`. The name on the left is matched without regard to upper or lower case. Only needed when the two names really differ: finding an existing Jellyfin account by name already ignores case.
  - `logoutUrl`: string. Where `/SSOViews/logout` sends the browser to end the session at the provider. Leave empty to use the provider's `end_session_endpoint`. Set it for providers that do not publish one, such as Authelia (`https://auth.example.com/logout`).
  - `useClientSecretBasic`: boolean. Sends the client id and secret in an HTTP Basic authorization header instead of the request body. Pushed Authorization Requests always use the header, so turn this on, and set the provider to `client_secret_basic`, if you want Pushed Authorization enabled. Leave it off for a provider set to `client_secret_post`, and disable Pushed Authorization there instead.
  - `disableHttps`: boolean. Determines whether the OpenID discovery endpoint requires HTTPS.
  - `doNotValidateEndpoints`: boolean. Determines whether the OpenID discovery process will validate endpoints. This may be required for Google.
  - `doNotValidateIssuerName`: boolean. Determines whether the OpenID discovery process will validate the OpenID issuer name.
  - `schemeOverride`: string. Sets the scheme for URLs used. Can be useful if the plugin refuses to use HTTPS URLs.
- GET `OID/Del/PROVIDER_NAME`: This removes a configuration for OpenID for a given provider name.
- GET `OID/Get`: Lists the configurations currently available.
- GET `OID/States`: Lists currently active OpenID flows in progress.

### Plugin-Wide Settings

These are not per-provider. Set them in the admin page under **SSO-Only Login**,
or through the normal Jellyfin plugin configuration API
(`GET`/`POST /Plugins/505ce9d1d91642fa86ca673ef241d7df/Configuration`):

- `enforceSsoOnly`: boolean. Turns off password login for every account that has an SSO link. Accounts without a link keep their password. Defaults to `false`.
- `ssoOnlyExemptUsernames`: array of strings. Jellyfin usernames that keep password login anyway. Matched without regard to upper or lower case.

### Misc

- POST `Unregister/username`: This "unregisters" a user from SSO. A JSON-formatted string must be posted with the new authentication provider. To reset to the default provider, use `Jellyfin.Server.Implementations.Users.DefaultAuthenticationProvider` like so: `curl -X POST -H "Content-Type: application/json" -d '"Jellyfin.Server.Implementations.Users.DefaultAuthenticationProvider"' "https://myjellyfin.example.com/sso/Unregister/username?api_key=API_KEY`

## Limitations

Logging in with an SSO account that has the same username as an existing, unlinked Jellyfin account adopts that account (and, with `enableAuthorization`, overrides its permissions). Use caution when overriding the administrator account! Set `disableUsernameAccountAdoption` on the provider once your users are linked to turn this off.

> When `enableAuthorization` is on, permissions (including the administrator flag) are synced from the provider's roles on every login: a login that does not match any `adminRoles` entry revokes admin, unless `preserveAdminPermissions` is enabled on the provider. Make sure your role mapping is correct before logging in with an admin account.

~~There is no GUI to sign in. You have to make it yourself! The buttons should redirect to something like this: [https://myjellyfin.example.com/sso/SAML/start/clientid](https://myjellyfin.example.com/sso/SAML/start/clientid) replacing `clientid` with the provider client ID and `SAML` with the auth scheme (either `SAML` or `OID`).~~

~~Furthermore, there is no functional admin page (yet). PRs for this are welcome. In the meantime, you have to interact with the API to add or remove configurations.~~ Added by [strazto](https://github.com/strazto) in PR [#18](https://github.com/9p4/jellyfin-plugin-sso/pull/18) and [#27](https://github.com/9p4/jellyfin-plugin-sso/pull/27).

~~There is also no logout callback. Logging out of Jellyfin will log you out of Jellyfin only, instead of the SSO provider as well.~~ As of 6.1.0.0 the page at `/SSOViews/logout?provider=NAME` ends both sessions. Jellyfin 12 has no hook in its own sign-out button, so the link has to be placed by hand; see [Signing Out Of The Provider As Well](#signing-out-of-the-provider-as-well).

~~This only supports Jellyfin on its own domain (for now). This is because I'm using string concatenation for generating some URLs. A PR is welcome to patch this.~~ Fixed in [PR #1](https://github.com/9p4/jellyfin-plugin-sso/pull/1).

~~**This only works on the web UI**.~~ As of 5.0.0.0 the flow also completes inside the native Jellyfin mobile apps (Android, and the Expo-based iOS app): instead of waiting for an iframed web client to seed `localStorage`, the plugin now builds the credentials directly from the auth response and applies them in the top frame, which the native apps pick up on their next `Sessions/Capabilities/Full` request. ~~The user must open the Jellyfin web UI BEFORE using the SSO program to populate some values in the localStorage.~~ Fixed by implementing a comment by [Pfuenzle](https://github.com/Pfuenzle) in [Issue #5](https://github.com/9p4/jellyfin-plugin-sso/issues/5#issuecomment-1041864820).

# Contributing

## Dependencies

This project uses Nix flakes to manage development dependencies. Run `nix develop` to use the same toolchain versions.

## Building

This is built with .NET 10.0 (required by Jellyfin 12). Build with `dotnet publish .` for the debug release in the `SSO-Auth` directory. Copy over the `Duende.IdentityModel.OidcClient.dll`, the `Duende.IdentityModel.dll`, the `Microsoft.IdentityModel.*.dll` files and the `SSO-Auth.dll` file in the `/bin/Debug/net10.0/publish` directory to a new folder in your Jellyfin configuration: `config/plugins/sso`.

### VSCode Workflow

An example `.vscode` configuration may be found at [strazto/jellyfin-plugin-sso-vscode](https://github.com/strazto/jellyfin-plugin-sso-vscode).

From the root of this repo, you may clone that to `.vscode`

```bash
# From repo root

git clone https://github.com/strazto/jellyfin-plugin-sso-vscode .vscode
```

## Testing

Unit tests (xUnit + Moq, targeting `net10.0`) live in `SSO-Auth.Tests/`:

```bash
dotnet test
```

There is also an **end-to-end SSO login test** under [`test/e2e/`](test/e2e/) that
brings up [authentik](https://goauthentik.io/) (as an OIDC provider) and
Jellyfin 12 in Docker with the built plugin, drives a real browser login with
Playwright, and verifies the plugin provisions a Jellyfin user:

```bash
cd test/e2e && ./run.sh
```

See [`test/e2e/README.md`](test/e2e/README.md) for details. It also runs in CI
(`.github/workflows/e2e.yml`) on pull requests.

## Releasing

This plugin uses [JPRM](https://github.com/oddstr13/jellyfin-plugin-repository-manager) to build the plugin. Refer to the documentation there to install JPRM.

Build the zipped plugin with `jprm --verbosity=debug plugin build .`.

### CI Releases

Anything merged to the main branch will be built and published by our CI system.

Anything tagged/released as a formal Github release will also be built and published by our CI system.

If you wish to use releases from your own fork, refer to
[Installing](#installing), however, you will need to change the url to the
manifest file, `https://raw.githubusercontent.com/MaxRink/jellyfin-plugin-sso/manifest-release/manifest.json`
so that it refers to your fork.

## Acknowledgements

This Jellyfin 12 release consolidates work from the wider `jellyfin-plugin-sso` fork ecosystem. All upstream forks are licensed **GPL-3.0**, the same license as this project, and their authors are credited below (and in the individual commit history via `Co-authored-by` trailers where applicable):

- **[AlexBocken](https://github.com/AlexBocken/jellyfin-plugin-sso)** — native mobile-app (Android / Expo iOS) SSO login support with error surfacing, the restyled sign-in handoff page, and the stale-canonical-link login fix.
- **[Buco7854](https://github.com/Buco7854/jellyfin-plugin-sso)** (Arnaud Grimbert) — `preserveAdminPermissions` option so logins no longer silently revoke admin when role mapping doesn't match, persisting role-mapped permissions through `UpdatePolicyAsync` (#367), the secured account-linking flow (authenticated `StartLink`, single-use provider-bound states, SAML `InResponseTo` checks) and its reworked self-service page, the Jellyfin 12 linking-page API client, the package-name alignment, and routing provider requests through Jellyfin's HTTP client with clear timeout errors.
- **[derSoerrn95](https://github.com/derSoerrn95/jellyfin-plugin-oidc)** (Sören Borgstedt) — a series of hardening fixes: escaping provider-supplied values on the linking page, not logging claim values on denied logins, advertising only enabled providers, null-safe scopes and roles, persisting the redirect path style and the unregister provider switch, materialising link queries, disposing avatar downloads, 404 on unlinking unknown names, reporting the real server version on SSO sessions, warnings for relaxed discovery checks, and the opt-out from adopting local accounts by username.
- **[ZigZagT](https://github.com/ZigZagT/jellyfin-plugin-sso)** — graceful handling of Cloudflare Access's UserInfo endpoint, `roleClaim` accepting several claim paths, always honouring `enabledFolders`, and config-page link/help-text fixes.
- **[vanutp](https://github.com/vanutp/jellyfin-plugin-sso)** (Ivan Filipenkov) — keying OpenID links on the `sub` claim instead of the username.
- **[kiliankoe](https://github.com/kiliankoe/jellyfin-plugin-sso)** (Kilian Koeltzsch) — rebasing the sub-keyed linking onto the linking flow, migrating username-keyed links, and removing the unused F# helper project; several of the commits above were adopted from this fork's consolidated history.
- **[michaelkuty](https://github.com/michaelkuty/jellyfin-plugin-sso)** — roles encoded as JSON object keys (Zitadel).
- **[eivarin](https://github.com/eivarin/jellyfin-plugin-sso)** — inline `data:` avatar images (Kanidm, Pocket ID).
- **[dangerouslaser](https://github.com/dangerouslaser/jellyfin-plugin-oidc)** — keeping pre-existing accounts on their own auth provider and only stripping default library access when the plugin manages permissions.
- **[ghadfield32](https://github.com/ghadfield32/jellyfin-plugin-sso)** (Geoffrey) — resetting checkbox state when switching providers in the admin page.
- **[basil-squared](https://github.com/basil-squared/Authentikate)** — account-linking fix that carries the linking user id through `TimedAuthorizeState`.
- **[dustinyschild](https://github.com/dustinyschild/jellyfin-plugin-sso)** — the OID device-code-flow authentication endpoint (`POST OID/DeviceAuth/{provider}`, RFC 8628).
- **[primeral](https://github.com/primeral/jellyfin-plugin-ssoplus)** — carrying a Quick Connect code through the OIDC login.
- **[athendrix / eddymoulton](https://github.com/eddymoulton/jellyfin-plugin-oidc)** — creating SSO users without default access to all library folders (#29), and the `UpdatePolicyAsync` persistence approach for Jellyfin 10.11+/12 (jellyfin/jellyfin#16298).

## Credits and Thanks

Much thanks to the [Jellyfin LDAP plugin](https://github.com/jellyfin/jellyfin-plugin-ldapauth) for offering a base for me to start on my plugin.

I use the [AspNet SAML](https://github.com/jitbit/AspNetSaml/) library for the SAML side of things (patched to work with Base64 on non-Windows machines).

I use the [Duende IdentityModel OIDC Client](https://github.com/DuendeSoftware/foss) library for the OpenID side of things.

Thanks to these projects, without which I would have been pulling my hair out implementing these protocols from scratch.

## Something funny about the origins of this plugin

It totally slipped my mind, but I had [requested this functionality a few years back](https://github.com/jellyfin/jellyfin/issues/2012). What goes around comes around, I guess.
