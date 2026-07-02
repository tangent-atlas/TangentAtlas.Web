<#
.SYNOPSIS
    Builds (or rebuilds) the tangent-atlas-development SQL server and the
    atlas-ledger database.

.EXAMPLE
    .\Deploy-AtlasDatabase.ps1
        Creates the server and/or database if missing.

.EXAMPLE
    .\Deploy-AtlasDatabase.ps1 -Rebuild
        DELETES the database (data loss!) and recreates it empty.
        You will be asked to type the database name to confirm.

.NOTES
    "Rebuild" recreates an EMPTY database. It does not restore data - if you
    need the data back, restore from a backup separately.
#>
[CmdletBinding()]
param(
    [switch]$Rebuild,
    [string]$ConfigPath = "$PSScriptRoot\AzureConfig.psd1"
)

. "$PSScriptRoot\Common.ps1"

# Stop on the first real error instead of cascading. Per-call
# -ErrorAction SilentlyContinue (the Get/Remove checks below) still overrides
# this, so existence probes remain non-fatal.
$ErrorActionPreference = 'Stop'

$shared = Import-PowerShellDataFile -Path $ConfigPath

# --- This resource's own attributes (live with the script that builds it) ---
$rgKey = 'Development'
$db = @{
    ServerName            = 'tangent-atlas-development'
    DatabaseName          = 'atlas-ledger'
    Edition               = 'GeneralPurpose'
    ComputeModel          = 'Serverless'
    ComputeGeneration     = 'Gen5'
    VCore                 = 1
    MinCapacity           = 0.5
    AutoPauseDelayMinutes = 60
    MaxSizeBytes          = 34359738368   # 32 GiB
    EntraOnly             = $true         # Entra-only auth; admin comes from shared config
    AllowAzureServices    = $true

    # SQL-auth fallback - only used if EntraOnly is $false:
    # AdminLogin          = 'sqladmin'
    # AdminPasswordSecret = 'sql-admin-password'   # name of a secret in the shared vault
}

$rg = $shared.ResourceGroups[$rgKey]

Write-Host "`n=== atlas-ledger database deployment ===" -ForegroundColor Green
Initialize-AzureSession -SubscriptionId $shared.SubscriptionId
Ensure-ResourceGroup   -Name $rg.Name -Location $rg.Location

if ($Rebuild) {
    Write-Warning "Rebuild will DELETE database '$($db.DatabaseName)' and ALL its data."
    Write-Warning "This does NOT restore data - restore from a backup separately if needed."
    $confirm = Read-Host "Type the database name to confirm deletion"
    if ($confirm -ne $db.DatabaseName) { throw "Confirmation did not match. Aborting." }

    Remove-AzSqlDatabase -ResourceGroupName $rg.Name -ServerName $db.ServerName `
        -DatabaseName $db.DatabaseName -Force -ErrorAction SilentlyContinue
    # The server is left in place. To also rebuild the server, uncomment:
    # Remove-AzSqlServer -ResourceGroupName $rg.Name -ServerName $db.ServerName -Force -ErrorAction SilentlyContinue
}

# --- SQL server ---
$server = Get-AzSqlServer -ResourceGroupName $rg.Name -ServerName $db.ServerName -ErrorAction SilentlyContinue
if (-not $server) {

    if ($db.EntraOnly) {
        # Entra-only server: admin is a shared directory principal, SQL auth disabled.
        Write-Step "Creating SQL server '$($db.ServerName)' with Entra admin '$($shared.EntraAdmin.Name)'..."
        $params = @{
            ResourceGroupName     = $rg.Name
            ServerName            = $db.ServerName
            Location              = $rg.Location
            ExternalAdminName     = $shared.EntraAdmin.Name
            ExternalAdminSID      = [guid]$shared.EntraAdmin.ObjectId
            EnableActiveDirectoryOnlyAuthentication = $true
        }
        $server = New-AzSqlServer @params
    }
    else {
        # SQL-auth fallback: create with a login + password from the shared vault.
        Write-Step "Creating SQL server '$($db.ServerName)' with SQL admin '$($db.AdminLogin)'..."
        if ($shared.KeyVaultName -and $db.AdminPasswordSecret) {
            $password = Get-AzureSecret -VaultName $shared.KeyVaultName -SecretName $db.AdminPasswordSecret
            $cred = New-Object System.Management.Automation.PSCredential ($db.AdminLogin, $password)
        } else {
            $cred = Get-Credential -UserName $db.AdminLogin -Message "Enter the SQL admin password"
        }
        $server = New-AzSqlServer -ResourceGroupName $rg.Name -ServerName $db.ServerName `
            -Location $rg.Location -SqlAdministratorCredentials $cred
    }

} else {
    Write-Step "SQL server '$($db.ServerName)' already exists."
}

# --- Firewall: allow other Azure services (e.g. the Web App) to connect ---
if ($db.AllowAzureServices) {
    if (-not (Get-AzSqlServerFirewallRule -ResourceGroupName $rg.Name -ServerName $db.ServerName `
                -FirewallRuleName 'AllowAllAzureIPs' -ErrorAction SilentlyContinue)) {
        Write-Step "Adding 'Allow Azure services' firewall rule..."
        New-AzSqlServerFirewallRule -ResourceGroupName $rg.Name -ServerName $db.ServerName `
            -AllowAllAzureIPs | Out-Null
    }
}

# --- SQL database ---
$database = Get-AzSqlDatabase -ResourceGroupName $rg.Name -ServerName $db.ServerName `
    -DatabaseName $db.DatabaseName -ErrorAction SilentlyContinue
if (-not $database) {
    Write-Step "Creating database '$($db.DatabaseName)' ($($db.Edition) $($db.ComputeModel) $($db.ComputeGeneration) $($db.VCore) vCore)..."
    New-AzSqlDatabase -ResourceGroupName $rg.Name -ServerName $db.ServerName `
        -DatabaseName $db.DatabaseName `
        -Edition $db.Edition -ComputeModel $db.ComputeModel `
        -ComputeGeneration $db.ComputeGeneration -VCore $db.VCore `
        -MinimumCapacity $db.MinCapacity -AutoPauseDelayInMinutes $db.AutoPauseDelayMinutes `
        -MaxSizeBytes $db.MaxSizeBytes | Out-Null
} else {
    Write-Step "Database '$($db.DatabaseName)' already exists."
}

Write-Host "Done. $($db.ServerName).database.windows.net / $($db.DatabaseName)" -ForegroundColor Green
