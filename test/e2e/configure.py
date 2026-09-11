#!/usr/bin/env python3
"""Configure authentik (OIDC provider + application) and Jellyfin (startup wizard
+ SSO plugin OIDC provider) for the end-to-end SSO login test.

Idempotent-ish: safe to re-run. Uses only the Python standard library.

Env (with defaults):
  AK_URL            authentik base URL as seen from this script   (http://localhost:9000)
  JF_URL            Jellyfin base URL as seen from this script     (http://127.0.0.1:8096)
  AK_INTERNAL       authentik base URL as seen from inside the network (http://authentik-server:9000)
  JF_INTERNAL_HOST  Jellyfin host:port as seen from inside the network  (jellyfin:8096)
  AK_TOKEN          authentik bootstrap API token                  (authentik-bootstrap-token)
"""
import json
import os
import time
import urllib.request
import urllib.error

AK = os.environ.get("AK_URL", "http://localhost:9000")
JF = os.environ.get("JF_URL", "http://127.0.0.1:8096")
AK_INTERNAL = os.environ.get("AK_INTERNAL", "http://authentik-server:9000")
JF_INTERNAL_HOST = os.environ.get("JF_INTERNAL_HOST", "jellyfin:8096")
AK_TOKEN = os.environ.get("AK_TOKEN", "authentik-bootstrap-token")

CLIENT_ID = "jellyfin-client"
CLIENT_SECRET = "jellyfin-secret"
PROVIDER_SLUG = "jellyfin"


def req(method, url, headers=None, body=None, timeout=30):
    data = json.dumps(body).encode() if body is not None else None
    r = urllib.request.Request(url, data=data, method=method, headers=headers or {})
    try:
        with urllib.request.urlopen(r, timeout=timeout) as resp:
            raw = resp.read().decode()
            return resp.status, (json.loads(raw) if raw else None)
    except urllib.error.HTTPError as e:
        raw = e.read().decode()
        try:
            return e.code, json.loads(raw)
        except Exception:
            return e.code, raw
    except Exception as e:
        # connection reset / refused / DNS etc. during service startup
        return 0, str(e)


def ak(method, path, body=None):
    return req(method, AK + path,
               {"Authorization": f"Bearer {AK_TOKEN}", "Content-Type": "application/json"}, body)


def lookup(path):
    st, d = ak("GET", path)
    assert st == 200, f"lookup {path} -> {st} {d}"
    return d["results"][0]["pk"] if d["results"] else None


def wait_ready():
    print("waiting for authentik and jellyfin...")
    ok_health = False
    for _ in range(120):
        s1, _ = req("GET", AK + "/-/health/ready/")
        s2, info = req("GET", JF + "/System/Info/Public")
        # authentik's bootstrap token is created asynchronously by the worker;
        # wait until it is actually usable, not just until health is up.
        s3, _ = ak("GET", "/api/v3/core/users/me/")
        if s1 == 200 and s2 == 200 and s3 == 200:
            print("  both services responding and authentik token valid")
            return
        time.sleep(5)
    raise SystemExit("services did not become ready in time")


def configure_authentik():
    print("== authentik ==")
    authz = lookup("/api/v3/flows/instances/?slug=default-provider-authorization-implicit-consent")
    invalidation = lookup("/api/v3/flows/instances/?slug=default-provider-invalidation-flow")
    authn = lookup("/api/v3/flows/instances/?slug=default-authentication-flow")
    signing = lookup("/api/v3/crypto/certificatekeypairs/?has_key=true")
    st, d = ak("GET", "/api/v3/propertymappings/provider/scope/")
    scopes = {m["scope_name"]: m["pk"] for m in d["results"]}
    scope_pks = [scopes["openid"], scopes["email"], scopes["profile"]]

    redirect = f"http://{JF_INTERNAL_HOST}/sso/OID/redirect/{PROVIDER_SLUG}"
    provider = {
        "name": "jellyfin",
        "authorization_flow": authz,
        "invalidation_flow": invalidation,
        "authentication_flow": authn,
        "client_type": "confidential",
        "client_id": CLIENT_ID,
        "client_secret": CLIENT_SECRET,
        "signing_key": signing,
        "redirect_uris": [{"matching_mode": "strict", "url": redirect}],
        "property_mappings": scope_pks,
        "sub_mode": "user_username",
        "include_claims_in_id_token": True,
        "issuer_mode": "per_provider",
    }
    st, d = ak("GET", "/api/v3/providers/oauth2/?name=jellyfin")
    if d["results"]:
        pk = d["results"][0]["pk"]
        ak("PATCH", f"/api/v3/providers/oauth2/{pk}/", provider)
        print(f"  provider updated pk={pk}")
    else:
        st, d = ak("POST", "/api/v3/providers/oauth2/", provider)
        assert st in (200, 201), f"create provider -> {st} {d}"
        pk = d["pk"]
        print(f"  provider created pk={pk}")

    st, d = ak("GET", f"/api/v3/core/applications/?slug={PROVIDER_SLUG}")
    if not d["results"]:
        st, d = ak("POST", "/api/v3/core/applications/",
                   {"name": "Jellyfin", "slug": PROVIDER_SLUG, "provider": pk})
        assert st in (200, 201), f"create application -> {st} {d}"
        print("  application created")
    else:
        print("  application already exists")

    issuer = f"{AK_INTERNAL}/application/o/{PROVIDER_SLUG}/"
    print(f"  issuer (internal): {issuer}")
    return issuer


def jf_headers(token=None):
    auth = 'MediaBrowser Client="e2e", Device="e2e", DeviceId="e2e-device", Version="1.0.0"'
    if token:
        auth = f'MediaBrowser Token="{token}"'
    return {"Authorization": auth, "Content-Type": "application/json"}


def jf_root_token():
    # Jellyfin 12 lazily creates a default admin named "root" with an empty
    # password. Authenticate as root to obtain an admin token.
    for _ in range(20):
        st, d = req("POST", JF + "/Users/AuthenticateByName", jf_headers(),
                    {"Username": "root", "Pw": ""})
        if st == 200:
            return d["AccessToken"]
        time.sleep(2)
    raise SystemExit("could not authenticate as root")


def configure_jellyfin(issuer):
    print("== jellyfin ==")
    st, info = req("GET", JF + "/System/Info/Public")
    if not info.get("StartupWizardCompleted"):
        print("  completing startup wizard...")
        req("POST", JF + "/Startup/Configuration", jf_headers(),
            {"UICulture": "en-US", "MetadataCountryCode": "US", "PreferredMetadataLanguage": "en"})
        req("GET", JF + "/Startup/User", jf_headers())
        req("POST", JF + "/Startup/RemoteAccess", jf_headers(),
            {"EnableRemoteAccess": True, "EnableAutomaticPortMapping": False})
        st, _ = req("POST", JF + "/Startup/Complete", jf_headers())
        print(f"  startup complete ({st})")

    token = jf_root_token()
    print("  admin (root) token obtained")

    oid_cfg = {
        "oidEndpoint": issuer,
        "oidClientId": CLIENT_ID,
        "oidSecret": CLIENT_SECRET,
        "enabled": True,
        "enableAuthorization": True,
        "enableAllFolders": True,
        "enableFolderRoles": False,
        "enableLiveTvRoles": False,
        "enableLiveTv": True,
        "enableLiveTvManagement": False,
        "roles": [],
        "adminRoles": [],
        "folderRoleMapping": [],
        "liveTvRoles": [],
        "liveTvManagementRoles": [],
        "enabledFolders": [],
        "oidScopes": ["email", "profile"],
        "canonicalLinks": {},
        "roleClaim": "groups",
        "defaultProvider": "",
        "disableHttps": True,
        "doNotValidateEndpoints": False,
        "doNotValidateIssuerName": False,
    }
    st, d = req("POST", f"{JF}/sso/OID/Add/{PROVIDER_SLUG}", jf_headers(token), oid_cfg)
    assert st in (200, 204), f"add oid config -> {st} {d}"
    st, d = req("GET", f"{JF}/sso/OID/Get", jf_headers(token))
    assert isinstance(d, dict) and PROVIDER_SLUG in d, f"provider not registered: {d}"
    print(f"  plugin OIDC provider '{PROVIDER_SLUG}' configured and verified")
    return token


if __name__ == "__main__":
    wait_ready()
    issuer = configure_authentik()
    token = configure_jellyfin(issuer)
    print("\nCONFIG DONE")
    print(f"JF_ADMIN_TOKEN={token}")
