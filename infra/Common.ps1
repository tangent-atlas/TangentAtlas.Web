# ---------------------------------------------------------------------------
# Common.ps1 - shared helpers dot-sourced by every deploy script.
# ---------------------------------------------------------------------------

function Write-Step {
    param([Parameter(Mandatory)][string]$Message)
    Write-Host "  -> $Message" -ForegroundColor Cyan
}

# Make sure the Az module is present, we are signed in, and the correct subscription is selected.
function Initialize-AzureSession {
    param([Parameter(Mandatory)][string]$SubscriptionId)

    if (-not (Get-Module -ListAvailable -Name Az.Accounts)) {
        throw "The Az PowerShell module is not installed. Run:  Install-Module Az -Scope CurrentUser"
    }

    if (-not (Get-AzContext)) {
        Write-Step "No Azure session found - launching sign-in..."
        Connect-AzAccount | Out-Null
    }

    Write-Step "Selecting subscription '$SubscriptionId'..."
    Set-AzContext -Subscription $SubscriptionId | Out-Null
}

# Create a resource group only if it does not already exist (idempotent).
function Ensure-ResourceGroup {
    param([Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)][string]$Location)
    if (-not (Get-AzResourceGroup -Name $Name -ErrorAction SilentlyContinue)) {
        Write-Step "Creating resource group '$Name' in $Location..."
        New-AzResourceGroup -Name $Name -Location $Location | Out-Null
    } else {
        Write-Step "Resource group '$Name' already exists."
    }
}

# Pull a secret from Key Vault and return it as a SecureString.
function Get-AzureSecret {
    param([Parameter(Mandatory)][string]$VaultName, [Parameter(Mandatory)][string]$SecretName)
    $secret = Get-AzKeyVaultSecret -VaultName $VaultName -Name $SecretName -ErrorAction SilentlyContinue
    if (-not $secret) {
        throw "Secret '$SecretName' was not found in Key Vault '$VaultName'."
    }
    return $secret.SecretValue
}
