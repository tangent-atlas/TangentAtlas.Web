# Azure resource rebuild scripts

PowerShell (Az module) scripts to build and rebuild Azure resources on demand.
Currently covers the **corporate website** (App Service plan + Web App) and the
**atlas-ledger database** (SQL server + serverless database).

## Architecture

Two kinds of settings, kept deliberately separate:

- **Shared / corporate symbols** live in `AzureConfig.psd1` - and ONLY these. A
  value belongs there only if more than one resource references it
  (subscription, resource groups, the Key Vault, the org admin principal). This
  keeps the config file small no matter how many resources you add.
- **Per-resource attributes** live inside the deploy script that builds that
  resource (the `$site = @{...}` / `$db = @{...}` block at the top). Changing a
  value that affects exactly one resource means editing exactly one script.

The test: if changing a value should change several resources, it's shared; if
it changes one, it lives with that resource.

## Files

| File | Purpose |
|------|---------|
| `AzureConfig.psd1` | Shared symbols only: subscription, resource groups, vault, Entra admin. |
| `Common.ps1` | Helpers (sign-in, resource group, Key Vault) dot-sourced by each script. |
| `Deploy-CorporateWebsite.ps1` | Builds/rebuilds the App Service plan + Web App. |
| `Deploy-AtlasDatabase.ps1` | Builds/rebuilds the SQL server + serverless database. |
| `Deploy-All.ps1` | Runs every deploy script in dependency order. |
| `Export-AzureInventory.ps1` | One-off bootstrap utility (see below) - NOT part of the IaC. |

## One-time setup

1. Install the Az module: `Install-Module Az -Scope CurrentUser`
2. Sign in: `Connect-AzAccount`
3. `AzureConfig.psd1` already has the real subscription and shared values. Edit
   it only when a genuinely shared symbol changes.

If a script won't run with a "not digitally signed" error, the files carry the
download "mark of the web": `Get-ChildItem .\*.ps1 | Unblock-File`. (Files pulled
from Git don't have this; it only affects copies saved directly.)

## Usage

```powershell
# Build anything missing; leave existing resources alone (idempotent, repeatable)
.\Deploy-AtlasDatabase.ps1
.\Deploy-CorporateWebsite.ps1

# Everything at once
.\Deploy-All.ps1

# Tear down and recreate
.\Deploy-AtlasDatabase.ps1 -Rebuild
.\Deploy-CorporateWebsite.ps1 -Rebuild

# Website with its custom domain + managed SSL (needs DNS pointing at the app first)
.\Deploy-CorporateWebsite.ps1 -WithCustomDomain
```

A run that reports "already exists" down the line is the success signal for a
dry run - it proves the config matches reality. Note this is *ensure-state*, not
a read-only `-WhatIf`: it creates anything missing. The only thing it changes on
an otherwise-complete environment is adding the "Allow Azure services" firewall
rule if absent.

## Important caveats

- **Database `-Rebuild` deletes all data** and recreates the database empty
  (it asks you to type the name to confirm). It does not restore data - that's a
  separate backup/restore concern.
- **The SQL server is Entra-only auth.** It's rebuilt with the shared Entra admin
  and SQL auth disabled. There's no SQL password to manage.
- **Globally-unique names don't release instantly.** A deleted Web App holds its
  name for up to 30 days (no manual purge); recover it with
  `Restore-AzDeletedWebApp` or use a different name meanwhile.

## The export script

`Export-AzureInventory.ps1` is scaffolding for getting TO infrastructure-as-code,
not part of it. Its only job is capturing the live config of resources that
don't yet have a build script - so you can write one from real values. Once a
resource has a faithful `Deploy-*.ps1`, exporting it is redundant. Use it to
capture the managed identities and Key Vault, then retire it. It is not drift
detection (it dumps state; it doesn't diff live-vs-desired).

## Rebuild dependency order

1. Resource groups (`Marketing`, `Development`)
2. Key Vault - recover-or-create, then repopulate secrets   *(script pending)*
3. User-assigned managed identities + federated credentials + roles   *(script pending)*
4. SQL server + database   *(done)*
5. App Service plan + Web App; attach identity, wire vault + DB connection   *(infra done; wiring pending)*
6. Post: grant the app's identity a database user; bind custom domain once DNS resolves

## Not yet scripted

- `Deploy-KeyVault.ps1` and `Deploy-ManagedIdentities.ps1` - these need the real
  federated-credential subjects, role assignments, and secret names first.
  Capture them with `Export-AzureInventory.ps1` (or from a restored Web App's
  config) before writing these.
- The app's database user (`CREATE USER ... FROM EXTERNAL PROVIDER`) - the SQL
  step that re-admits the web app's identity after a rebuild.
