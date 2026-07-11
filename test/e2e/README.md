# End-to-end SSO login test

This harness runs a **real** single-sign-on login against a live identity
provider to prove the plugin works on Jellyfin 12 — not just that it loads.

It brings up, in Docker:

- **authentik** (OIDC identity provider) + its PostgreSQL and Redis
- **Jellyfin 12** with the freshly built SSO plugin installed

then configures both, drives an actual browser OIDC login with **Playwright**
(user logs in at authentik, gets redirected back to Jellyfin), and finally
verifies **server-side** that the plugin provisioned a Jellyfin user for the
authentik account.

All services share one Docker network so the OIDC issuer and redirect URLs are
identical for the browser and for Jellyfin's server-to-server calls
(`http://authentik-server:9000/...`, `http://jellyfin:8096/...`).

## Requirements

- Docker + Docker Compose
- .NET 10 SDK and [`jprm`](https://github.com/oddstr13/jellyfin-plugin-repository-manager)
  (`pip install jprm`) — only if you let the harness build the plugin itself
- `python3` and `node`/`npm` on the host

## Run it

```bash
cd test/e2e
./run.sh                 # builds the plugin with jprm, runs the test, tears down
```

Or point it at a prebuilt plugin artifact (skips the build):

```bash
PLUGIN_ZIP=/path/to/sso-authentication_6.0.0.0.zip ./run.sh
```

Useful env vars:

- `KEEP_UP=1` — leave the stack running after the test (for debugging). Inspect
  Jellyfin at <http://localhost:8096> and authentik at <http://localhost:9000>
  (admin `akadmin` / `akadmin-password`).
- `PLAYWRIGHT_IMAGE` — override the Playwright container image.

A successful run ends with:

```
E2E SSO LOGIN TEST: PASS
```

## What each piece does

| File                      | Purpose                                                                                                                                                         |
| ------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `docker-compose.yml`      | The authentik + Jellyfin 12 stack, on one bridge network.                                                                                                       |
| `configure.py`            | Creates the authentik OAuth2/OIDC provider + application, completes the Jellyfin startup wizard, and registers the plugin's OIDC provider via the Jellyfin API. |
| `playwright/sso.test.mjs` | Opens `/sso/OID/start/jellyfin`, logs in at authentik, and asserts Jellyfin credentials are issued.                                                             |
| `run.sh`                  | Orchestrates build → up → configure → Playwright → server-side verify → teardown.                                                                               |

## In CI

`.github/workflows/e2e.yml` runs this on pull requests to `main` and on manual
dispatch. It builds the plugin, runs `run.sh`, and uploads the Playwright
screenshots (`test/e2e/playwright/shots/`) as an artifact.

## Notes

- The plugin ABI is `12.0.0.0`, so the stack pins `jellyfin/jellyfin:12.0-rc2`.
  Bump the tag in `docker-compose.yml` when Jellyfin 12 GA ships.
- authentik and Jellyfin versions are pinned in `docker-compose.yml`.
- Some Docker daemons inject an HTTP proxy into every container, which breaks
  container-to-container calls. The compose file disables the proxy for the
  Jellyfin container (all traffic here is internal); the Playwright container is
  started the same way in `run.sh`.
