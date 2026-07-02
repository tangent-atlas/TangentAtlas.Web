<#
.SYNOPSIS
    ONE-OFF BOOTSTRAP UTILITY - not part of the infrastructure-as-code.

    Captures the live state of resources you DON'T yet have a build script for,
    so you can write one from real values. Once a resource has a faithful
    Deploy-*.ps1, that script is the source of truth and exporting it is
    redundant - the build script already states everything this would.

    Use it to capture the managed identities and Key Vault (federated subjects,
    role assignments, secret names) before they're gone; then retire it.

    This is NOT drift detection - it only dumps state, it doesn't compare live
    against desired. Read-only: it changes nothing.

.NOTES
    Deliberately does NOT export Key Vault secret VALUES. Those are sensitive
    and must never land on disk (or in Git). It records secret NAMES so you
    know what must exist again - you are responsible for the values.
#>
[CmdletBinding()]
param(
    [string]$ConfigPath = "$PSScriptRoot\AzureConfig.psd1",
    [string]$OutputDir  = "$PSScriptRoot\inventory"
)

. "$PSScriptRoot\Common.ps1"

$config = Import-PowerShellDataFile -Path $ConfigPath
Initialize-AzureSession -SubscriptionId $config.SubscriptionId
New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

function Save-Json {
    param($Object, [string]$Name)
    $path = Join-Path $OutputDir "$Name.json"
    $Object | ConvertTo-Json -Depth 10 | Set-Content -Path $path -Encoding UTF8
    Write-Step "Saved $Name.json"
}

# 1. Full resource list - the reference map of everything in the subscription.
try {
    Write-Step "Capturing full resource list..."
    Get-AzResource | Select-Object Name, ResourceType, ResourceGroupName, Location, Id |
        Save-Json -Name 'all-resources'
} catch { Write-Warning "Resource list: $_" }

# 2. User-assigned identities + federated credentials + role assignments.
#    The Subject on each federated credential is what tells you WHO trusts it.
try {
    Write-Step "Capturing managed identities..."
    $identities = foreach ($id in Get-AzUserAssignedIdentity) {
        $fed   = Get-AzFederatedIdentityCredential -ResourceGroupName $id.ResourceGroupName -IdentityName $id.Name -ErrorAction SilentlyContinue
        $roles = Get-AzRoleAssignment -ObjectId $id.PrincipalId -ErrorAction SilentlyContinue
        [pscustomobject]@{
            Name                 = $id.Name
            ResourceGroup        = $id.ResourceGroupName
            Location             = $id.Location
            ClientId             = $id.ClientId
            PrincipalId          = $id.PrincipalId
            FederatedCredentials = $fed   | Select-Object Name, Issuer, Subject, Audience
            RoleAssignments      = $roles | Select-Object RoleDefinitionName, Scope
        }
    }
    $identities | Save-Json -Name 'managed-identities'
} catch { Write-Warning "Managed identities: $_" }

# 3. Key Vault: access model + secret/cert NAMES (never values).
try {
    Write-Step "Capturing Key Vault metadata..."
    $vaults = foreach ($v in Get-AzKeyVault) {
        $vault   = Get-AzKeyVault -VaultName $v.VaultName
        $secrets = Get-AzKeyVaultSecret      -VaultName $v.VaultName -ErrorAction SilentlyContinue | Select-Object Name, Enabled, Expires
        $certs   = Get-AzKeyVaultCertificate -VaultName $v.VaultName -ErrorAction SilentlyContinue | Select-Object Name, Enabled, Expires
        [pscustomobject]@{
            VaultName               = $vault.VaultName
            ResourceGroup           = $vault.ResourceGroupName
            Location                = $vault.Location
            Sku                     = $vault.Sku
            EnableRbacAuthorization = $vault.EnableRbacAuthorization
            EnablePurgeProtection   = $vault.EnablePurgeProtection
            AccessPolicies          = $vault.AccessPolicies
            SecretNames             = $secrets   # NAMES ONLY - no values
            CertificateNames        = $certs
        }
    }
    $vaults | Save-Json -Name 'key-vaults'
} catch { Write-Warning "Key Vault: $_" }

# 4. SQL servers + databases + auth model.
try {
    Write-Step "Capturing SQL configuration..."
    $sql = foreach ($srv in Get-AzSqlServer) {
        $aad  = Get-AzSqlServerActiveDirectoryAdministrator      -ResourceGroupName $srv.ResourceGroupName -ServerName $srv.ServerName -ErrorAction SilentlyContinue
        $only = Get-AzSqlServerActiveDirectoryOnlyAuthentication -ResourceGroupName $srv.ResourceGroupName -ServerName $srv.ServerName -ErrorAction SilentlyContinue
        $dbs  = Get-AzSqlDatabase -ResourceGroupName $srv.ResourceGroupName -ServerName $srv.ServerName |
                Where-Object DatabaseName -ne 'master' |
                Select-Object DatabaseName, Edition, CurrentServiceObjectiveName, MinimumCapacity, AutoPauseDelayInMinutes, MaxSizeBytes
        [pscustomobject]@{
            ServerName         = $srv.ServerName
            ResourceGroup      = $srv.ResourceGroupName
            Location           = $srv.Location
            EntraAdmin         = $aad.DisplayName
            EntraAdminObjectId = $aad.ObjectId
            EntraOnly          = $only.AzureADOnlyAuthentication
            Databases          = $dbs
        }
    }
    $sql | Save-Json -Name 'sql'
} catch { Write-Warning "SQL: $_" }

Write-Host "`nInventory written to $OutputDir" -ForegroundColor Green
Write-Warning "Secret VALUES were NOT captured. Confirm you can reproduce every secret listed in key-vaults.json before deleting the vault."
