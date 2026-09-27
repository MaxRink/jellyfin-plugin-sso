# Optional explicit identity and library migration

This is an opt-in change for Jellyfin 12. Installing the plugin does not migrate providers. Migrate one provider at a time after reviewing a preview.

The benefit is a clear distinction between identity and display name, and between manual library access and current SSO role grants:

- An existing account is matched only by a confirmed OIDC `sub` or SAML NameID under the pinned provider authority. Username fallback and automatic adoption are disabled for migrated providers, even if `DisableUsernameAccountAdoption` is false. Username mappings still name newly created accounts. Authenticated self-service linking remains available.
- A reviewed link pointing at a deleted account fails closed; it does not silently attach to a replacement account.
- When `EnableAuthorization` is enabled, library access is the union of current provider grants and explicit `Migration.ManualFolders` grants. Previous effective permissions are never inferred to be manual. A removed role therefore loses its library grant unless that library was separately granted manually. `PreserveUnmanagedFolders` has no effect after migration.
- `EnableAllFolders` or an explicit manual `AllFolders` grant still gives all-library access. With `EnableAuthorization` disabled, the plugin continues to leave permissions unchanged.

Admin, Live TV and download policy retain their existing settings. This is not a complete replacement of the account database: it does not add SCIM, account expiry, back-channel logout, cross-provider grant aggregation or immediate revocation of already-active sessions. Grants are recomputed at the next SSO login. New SSO-created accounts begin with no explicit manual grants.

OIDC pins the configured issuer URL; SAML pins the configured IdP endpoint. Changing that authority after migration blocks login/linking. SAML certificate rotation remains possible. Changing to a different identity authority requires an explicit review of its subjects and links; do not just edit the pin to suppress the guard.

## Prepare

Keep an independent Jellyfin data/database backup and a working local administrator login. Export the administrator-only `GET /Users` JSON response from Jellyfin immediately before stopping the server, saving it privately as `users.json`. The export provides account IDs, names and current effective library access; the tool does not access the live database or ask for an admin token.

Stop Jellyfin before applying or rolling back. Run the tool as the account that owns the plugin configuration. The `--server-stopped` argument is your explicit assertion that the server is stopped; the tool cannot discover every container or remote service. Preview can use a copied configuration, but apply must use the real file. Symlinks are refused on write. The tool writes files with owner-only permissions on Unix; use the Jellyfin service account so it can still read the result.

Use the .NET 10 SDK and the same source/plugin version. The configuration is normally under Jellyfin's `plugins/configurations` directory; locate the XML file containing this plugin's `OidConfigs` and `SamlConfigs`. Do not use Jellyfin's main `system.xml`.

```sh
dotnet run --project tools/SSO-Migration -- \
  preview /path/to/SSO-Auth.xml /private/users.json oid authentik /private/plan.json
```

For SAML, replace `oid` with `saml`; provider names are case-sensitive. Preview refuses to overwrite an existing plan. It contains no client secret or certificate, but account IDs, names and library access are private information.

## Review and apply

For every `Links` entry, fill `Subject` with the actual stable identifier from the IdP. Do not assume that `PreviousKey` or the displayed username is the subject. Keep `PreviousKey` and `UserId` unchanged. Missing accounts, duplicate subjects, missing entries and stale previews are rejected. If multiple old keys resolve to one subject, remove the obsolete link before taking a new preview; the tool deliberately refuses to guess which identity to merge.

Fill `ManualFolders` with one entry for **every distinct linked account**, using the hyphenated account ID shown in the plan. An empty grant is an explicit decision. Use library GUIDs, not names:

```json
{
  "ManualFolders": {
    "11111111-2222-3333-4444-555555555555": {
      "AllFolders": false,
      "Folders": ["aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"]
    }
  }
}
```

`CurrentFolders` shows effective access before migration for comparison only. It includes role-derived access, so blindly copying it would make old role grants permanent. It is never used as the new manual grant set. Confirm library IDs against Jellyfin. The tool validates GUID syntax; it cannot prove that a library still exists after the server is stopped.

Leave the preview fingerprint and issuer unchanged. After review:

```sh
dotnet run --project tools/SSO-Migration -- \
  apply /path/to/SSO-Auth.xml /private/users.json oid authentik /private/plan.json --server-stopped
```

Apply validates the complete plan in memory, creates an exclusive private backup and hash receipt next to the configuration, then replaces the file atomically after checking it did not change. Unknown configuration elements are rejected to avoid dropping data from a newer plugin. No user ID, password, database record or user policy is changed by the offline tool.

Start Jellyfin with the matching plugin and verify an existing user's login, account ID, library access and linking page. The plugin marks `PolicyApplied` before the first migrated login writes policy. Keep the backup and receipt outside public logs and repositories.

Manual grants can subsequently be changed through the existing administrator plugin-configuration API (`Migration.ManualFolders` within that provider). A dedicated grant editor is not included. Direct changes in Jellyfin's ordinary library-permissions editor are replaced at the next managed SSO login unless reflected in the explicit manual grants. Each provider has its own grants; the provider used for the latest login determines effective policy.

## Guarded rollback

Before a migrated login or any configuration change, stop Jellyfin and use the exact backup/receipt paths printed by apply:

```sh
dotnet run --project tools/SSO-Migration -- \
  rollback /path/to/SSO-Auth.xml /path/to/backup.before-migration \
  /path/to/backup.before-migration.receipt.json --server-stopped
```

Rollback requires the current file and the backup to match the receipt hashes. It restores the exact original bytes and retains both backup and receipt. A startup save, configuration edit or migrated login can invalidate rollback; refusal is intentional. After user policies have changed, restoring configuration alone would not undo those changes. Review account policies and restore the independent Jellyfin backup if a full rollback is needed. Never reset `PolicyApplied` or edit receipt hashes to force rollback.

## Validation

Behavioral tests cover complete/invalid plans, XML round trips, exact-byte CLI rollback, changed-file refusal, authority changes, username adoption refusal and manual-versus-role library access. The browser workflow logs in through authentik, migrates the stopped Jellyfin instance, then repeats login and linking and verifies that the same account ID remains and the migrated model was used.
