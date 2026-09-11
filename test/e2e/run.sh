#!/usr/bin/env bash
#
# End-to-end SSO login test: brings up authentik + Jellyfin 12 with the SSO
# plugin, configures both, and drives a real OIDC browser login with Playwright,
# then verifies server-side that the plugin provisioned a Jellyfin user.
#
# Usage:
#   PLUGIN_ZIP=/path/to/sso-authentication_X.Y.Z.zip ./run.sh        # use a prebuilt artifact
#   ./run.sh                                                          # build via jprm (needs dotnet + jprm)
#
# Env:
#   KEEP_UP=1     leave the stack running after the test (default: tear down)
#   PLAYWRIGHT_IMAGE   override the Playwright container image
#
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO="$(cd "$HERE/../.." && pwd)"
cd "$HERE"

PLAYWRIGHT_IMAGE="${PLAYWRIGHT_IMAGE:-mcr.microsoft.com/playwright:v1.55.0-noble}"
COMPOSE_NET="sso-e2e_ssonet"

# Jellyfin writes its config as root into the bind mount, so a non-root host
# user cannot delete it directly. Remove such dirs via a throwaway root
# container (falls back to a plain rm when that isn't possible).
nuke_dirs() {
  docker run --rm -v "$HERE:/w" alpine sh -c 'rm -rf /w/jf-config /w/jf-cache /w/playwright/shots' >/dev/null 2>&1 \
    || rm -rf "$HERE/jf-config" "$HERE/jf-cache" "$HERE/playwright/shots" 2>/dev/null || true
}

cleanup() {
  if [ "${KEEP_UP:-0}" != "1" ]; then
    echo "== tearing down =="
    docker compose down -v --remove-orphans >/dev/null 2>&1 || true
    nuke_dirs
  fi
}
trap cleanup EXIT

echo "== preparing plugin =="
nuke_dirs
mkdir -p "$HERE/jf-config/plugins/SSO-Auth" "$HERE/jf-cache" "$HERE/playwright/shots"

if [ -z "${PLUGIN_ZIP:-}" ]; then
  echo "  building plugin with jprm..."
  tmp="$(mktemp -d)"
  jprm plugin build "$REPO" --dotnet-framework net10.0 --output "$tmp"
  PLUGIN_ZIP="$(ls "$tmp"/*.zip | head -1)"
fi
echo "  installing plugin from $PLUGIN_ZIP"
unzip -o -q "$PLUGIN_ZIP" -d "$HERE/jf-config/plugins/SSO-Auth"

export AUTHENTIK_SECRET_KEY="${AUTHENTIK_SECRET_KEY:-$(python3 -c 'import secrets;print(secrets.token_urlsafe(50))')}"

echo "== starting stack =="
docker compose up -d

echo "== configuring authentik + jellyfin =="
python3 configure.py

echo "== running Playwright SSO login test =="
( cd playwright && npm ci --no-audit --no-fund >/dev/null 2>&1 || npm install --no-audit --no-fund )
docker run --rm --network "$COMPOSE_NET" --user root \
  -e HTTP_PROXY= -e HTTPS_PROXY= -e http_proxy= -e https_proxy= -e NO_PROXY='*' -e no_proxy='*' \
  -e PLAYWRIGHT_BROWSERS_PATH=/ms-playwright \
  -v "$HERE/playwright:/work" -w /work \
  "$PLAYWRIGHT_IMAGE" node sso.test.mjs
echo "  Playwright login test PASSED"

echo "== verifying plugin provisioned a Jellyfin user (server-side) =="
python3 - <<'PY'
import json, urllib.request
JF = "http://127.0.0.1:8096"
def get(path, token):
    r = urllib.request.Request(JF + path, headers={"Authorization": f'MediaBrowser Token="{token}"'})
    return json.load(urllib.request.urlopen(r, timeout=30))
def auth():
    import urllib.request as u
    body = json.dumps({"Username": "root", "Pw": ""}).encode()
    r = u.Request(JF + "/Users/AuthenticateByName", data=body,
                  headers={"Authorization": 'MediaBrowser Client="v", Device="v", DeviceId="v", Version="1"',
                           "Content-Type": "application/json"}, method="POST")
    return json.load(u.urlopen(r, timeout=30))["AccessToken"]
token = auth()
users = get("/Users", token)
sso = [u for u in users if "SSO_Auth" in (u["Policy"].get("AuthenticationProviderId") or "")]
assert sso, "no user was provisioned by the SSO plugin: " + str([u["Name"] for u in users])
print("  PASS: SSO-provisioned Jellyfin user(s):", [u["Name"] for u in sso])
PY

echo ""
echo "E2E SSO LOGIN TEST: PASS"
