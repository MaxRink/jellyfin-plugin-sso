# AGENTS.md

Guidance for automated agents and contributors working in this repository.

## What this is

`jellyfin-plugin-sso` is a Jellyfin authentication plugin that lets users sign in
through an SSO provider (OpenID Connect **and** SAML). This repository is a maintained
fork of the archived [9p4/jellyfin-plugin-sso](https://github.com/9p4/jellyfin-plugin-sso)
and targets **Jellyfin 12**.

## Target framework & Jellyfin version

- **Jellyfin 12** dropped the historical `10.` version prefix (10.11 → 12.0).
- Plugin targets **`net10.0`** and builds against `Jellyfin.Controller` / `Jellyfin.Model`
  **`12.0.0`**. Bump both packages together when a newer 12.x ships.
- `build.yaml` must stay in sync: `framework: "net10.0"`, `targetAbi: "12.0.0.0"`.
- Two client-authentication styles exist and they interact: Duende sends token-endpoint credentials
  in the body by default, but Pushed Authorization Requests always use an HTTP Basic header. A
  provider can only be configured one way, so `UseClientSecretBasic` switches the token request to
  Basic; use it together with `client_secret_basic` at the provider if Pushed Authorization should
  stay on, otherwise leave it off and disable Pushed Authorization.
- The logo ships inside the package. `build.yaml` has `image: "img/logo.png"`, jprm copies it in and
  records it as `image`, and a step in `.github/workflows/build.yml` adds the `imagePath` key that
  Jellyfin actually reads.
- Do **not** re-add an explicit `System.Security.Cryptography.Xml` package reference — it
  is provided by the net10 shared framework, and the standalone package pulls a vulnerable
  transitive `System.Security.Cryptography.Pkcs`.

## Project layout

- `SSO-Auth/` — the plugin (C#). `Api/SSOController.cs` holds the OIDC + SAML endpoints and
  the shared `Authenticate(...)` method; `WebResponse.cs` builds the browser/native-app
  login handoff HTML/JS; `Saml.cs` handles SAML; `Config/` holds the admin UI and the
  self-service linking page; `Views/apiClient.js` is the small authenticated API client the
  linking page uses (Jellyfin 12 ignores the legacy `X-Emby-Authorization` header, so it must
  send `Authorization: MediaBrowser ...`).
- `build.yaml` — JPRM plugin metadata (version, ABI, artifacts, changelog).

## Build / test / lint

Requires the **.NET 10 SDK**.

```bash
dotnet restore
dotnet build --no-restore --warnaserror   # CI enforces zero warnings
dotnet publish SSO-Auth/SSO-Auth.csproj -c Release -o out   # gathers dependency DLLs
```

`dotnet build` runs StyleCop / analyzers via `jellyfin.ruleset`; keep the build
warning-clean. Package the release plugin with JPRM: `jprm plugin build .`.

Unit tests live in `SSO-Auth.Tests/` (xUnit + Moq, `net10.0`) and run via
`dotnet test`.

## End-to-end SSO login test

`test/e2e/` contains a real end-to-end test: it spins up **authentik** (OIDC IdP)
and **Jellyfin 12** in Docker with the built plugin, drives an actual browser
OIDC login with Playwright, and verifies the plugin provisions a Jellyfin user.
Run it with `cd test/e2e && ./run.sh` (see `test/e2e/README.md`); it also runs in
CI via `.github/workflows/e2e.yml`. Keep the pinned `jellyfin/jellyfin` tag in
`test/e2e/docker-compose.yml` in sync with the plugin ABI. Note: Jellyfin 12
lazily creates a default admin named `root` with an empty password (used by the
test to obtain an admin token).

## Account linking and identity keys

- Linking starts only from the authenticated `POST {mode}/StartLink/{provider}` endpoint
  (called by `Config/linking.js`). States in `StateManager` / `SamlLinkStateManager` are
  single-use, bound to the issuing provider and expire after 10 minutes; SAML responses are
  checked with `Saml.Response.IsResponseTo(requestId, recipient)`.
- OpenID links (`CanonicalLinks`) are keyed by the `sub` claim, SAML links by the NameID.
  `CreateCanonicalLinkAndUserIfNotExist` still resolves links keyed by username from
  releases before 6.0 and rekeys them; it returns `null` (→ HTTP 409) when
  `DisableUsernameAccountAdoption` is set and the username belongs to an unlinked local user.

## SSO-only login

`Auth/SsoOnlyAuthProvider.cs` is an `IAuthenticationProvider` that always throws. Accounts whose
`AuthenticationProviderId` points at it cannot use a password. `Auth/SsoOnlyEnforcer.cs` decides
which accounts get moved there, and is called from three places: the plugin's
`UpdateConfiguration` (only when the setting or the exemption list changed, because the login flow
saves the configuration too), `EventConsumers/UserCreatedConsumer.cs`, and after a successful SSO
login. Accounts with no SSO link are deliberately left alone so one local administrator keeps a
password. `ServiceRegistrator.cs` registers the provider and the event consumer.

## Signing out

Jellyfin 12 has no server-side hook in its sign-out path, so `Views/logout.html` does the work in
the browser: it ends the Jellyfin session the way the web client does, then navigates to
`GET /sso/OID/logout/{provider}`, which redirects to the provider. That endpoint is anonymous on
purpose, because by then the browser has no token left. Prefer the provider's
`end_session_endpoint` from discovery; Authelia does not publish one, so its providers need
`LogoutUrl` set.

## Permissions persistence (important)

On Jellyfin 10.11+/12, `UpdateUserAsync` no longer persists modified permission rows
(jellyfin/jellyfin#16298). Always write user policy/permissions through
`_userManager.UpdatePolicyAsync(userId, policy)` and re-fetch the user afterwards (the
cached entity becomes stale). Any new `Authenticate(...)` call site must pass the
`preserveAdmin` argument.

## Adopting third-party / fork code

When incorporating code from other forks, verify license compatibility (this project and
its known forks are all GPL-3.0) and credit the original authors — via `Co-authored-by`
trailers on the commit and in the README **Acknowledgements** section.
