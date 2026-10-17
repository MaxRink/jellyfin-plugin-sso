#!/usr/bin/env python3
"""Exercise the offline migration against the stopped e2e Jellyfin instance."""
import json
import pathlib
import subprocess
import time
import urllib.request
import xml.etree.ElementTree as ET

HERE = pathlib.Path(__file__).resolve().parent
ROOT = HERE.parent.parent
JF = "http://127.0.0.1:8096"
PLUGIN = "505ce9d1-d916-42fa-86ca-673ef241d7df"


def api(path, token=None, body=None):
    headers = {"Authorization": 'MediaBrowser Client="migration-test", Device="test", DeviceId="migration-test", Version="1"', "Content-Type": "application/json"}
    if token:
        headers["Authorization"] = f'MediaBrowser Token="{token}"'
    data = json.dumps(body).encode() if body is not None else None
    with urllib.request.urlopen(urllib.request.Request(JF + path, headers=headers, data=data), timeout=30) as response:
        return json.load(response)


def admin():
    return api("/Users/AuthenticateByName", body={"Username": "root", "Pw": ""})["AccessToken"]


def run(*args):
    subprocess.run(args, cwd=HERE, check=True)


def tool(*args):
    run("dotnet", "run", "--project", str(ROOT / "tools/SSO-Migration"), "--", *map(str, args))


def prepare():
    token = admin()
    users = api("/Users", token)
    account = next(user for user in users if user["Name"] == "akadmin")
    state = HERE / "migration-state"
    state.mkdir(mode=0o700, exist_ok=True)
    (state / "users.json").write_text(json.dumps(users))
    (state / "user-id").write_text(account["Id"])
    run("docker", "compose", "stop", "jellyfin")
    # The Linux Jellyfin container owns these bind-mounted files as root.
    run("sudo", "chown", "-R", str(__import__("os").getuid()), str(HERE / "jf-config"))
    paths = [path for path in (HERE / "jf-config/plugins/configurations").glob("*.xml") if ET.parse(path).getroot().find("OidConfigs") is not None]
    assert len(paths) == 1, "expected exactly one SSO configuration"
    config = paths[0]
    plan_path = state / "plan.json"
    tool("preview", config, state / "users.json", "oid", "jellyfin", plan_path)
    plan = json.loads(plan_path.read_text())
    assert len(plan["Links"]) == 1
    # configure.py explicitly pins authentik sub_mode=user_username for this fixture.
    assert plan["Links"][0]["PreviousKey"] == "akadmin"
    plan["Links"][0]["Subject"] = "akadmin"
    plan["ManualFolders"] = {plan["Links"][0]["UserId"]: {"AllFolders": False, "Folders": []}}
    plan_path.write_text(json.dumps(plan))
    tool("apply", config, state / "users.json", "oid", "jellyfin", plan_path, "--server-stopped")
    run("docker", "compose", "start", "jellyfin")
    for attempt in range(90):
        try:
            admin()
            break
        except Exception:
            time.sleep(2)
    else:
        raise AssertionError("Jellyfin did not restart after migration")
    print("Migration applied; ready for the second browser login.")


def verify():
    token = admin()
    users = api("/Users", token)
    matches = [user for user in users if user["Name"] == "akadmin"]
    assert len(matches) == 1
    assert matches[0]["Id"] == (HERE / "migration-state/user-id").read_text(), "migration changed account identity"
    config = api(f"/Plugins/{PLUGIN}/Configuration", token)
    model = config["OidConfigs"]["jellyfin"]["Migration"]
    assert model["Enabled"] and model["PolicyApplied"], "explicit model was not used by login"
    print("MIGRATED SSO LOGIN: PASS; same account ID and explicit model applied.")


if __name__ == "__main__":
    import sys
    {"prepare": prepare, "verify": verify}[sys.argv[1]]()
