<#
.SYNOPSIS
    Builds (or rebuilds) the corporate-website App Service plan and Web App.

.EXAMPLE
    .\Deploy-CorporateWebsite.ps1
        Creates anything that is missing; leaves existing resources alone.

.EXAMPLE
    .\Deploy-CorporateWebsite.ps1 -Rebuild
        Deletes the Web App and plan, then recreates them from scratch.
#>
[CmdletBinding()]
param(
    [switch]$Rebuild,
    [switch]$WithCustomDomain,
    [string]$ConfigPath = "$PSScriptRoot\AzureConfig.psd1"
)

. "$PSScriptRoot\Common.ps1"

# Stop on the first real error instead of cascading past failures.
$ErrorActionPreference = 'Stop'

$shared = Import-PowerShellDataFile -Path $ConfigPath

# --- This resource's own attributes (live with the script that builds it) ---
$rgKey = 'Marketing'
$site = @{
    PlanName            = 'corporate-website'
    PlanTier            = 'Basic'      # Free | Basic | Standard | Premium | PremiumV2 | PremiumV3
    WorkerSize          = 'Small'      # Small | Medium | Large  (Basic + Small = B1)
    WorkerCount         = 1
    WebAppName          = 'corporate-website'
    HttpsOnly           = $true
    NetFrameworkVersion = 'v10.0'      # .NET 10 (Windows)
    CustomDomain        = 'www.tangentatlas.com'
}

$rg = $shared.ResourceGroups[$rgKey]

Write-Host "`n=== Corporate website deployment ===" -ForegroundColor Green
Initialize-AzureSession -SubscriptionId $shared.SubscriptionId
Ensure-ResourceGroup   -Name $rg.Name -Location $rg.Location

if ($Rebuild) {
    Write-Step "Rebuild requested - removing existing Web App and plan..."
    Remove-AzWebApp         -ResourceGroupName $rg.Name -Name $site.WebAppName -Force -ErrorAction SilentlyContinue
    Remove-AzAppServicePlan -ResourceGroupName $rg.Name -Name $site.PlanName   -Force -ErrorAction SilentlyContinue
}

# --- App Service plan ---
$plan = Get-AzAppServicePlan -ResourceGroupName $rg.Name -Name $site.PlanName -ErrorAction SilentlyContinue
if (-not $plan) {
    Write-Step "Creating App Service plan '$($site.PlanName)' ($($site.PlanTier)/$($site.WorkerSize) x$($site.WorkerCount))..."
    $plan = New-AzAppServicePlan -ResourceGroupName $rg.Name -Name $site.PlanName `
        -Location $rg.Location -Tier $site.PlanTier -WorkerSize $site.WorkerSize `
        -NumberofWorkers $site.WorkerCount
} else {
    Write-Step "App Service plan '$($site.PlanName)' already exists."
}

# --- Web App ---
$app = Get-AzWebApp -ResourceGroupName $rg.Name -Name $site.WebAppName -ErrorAction SilentlyContinue
if (-not $app) {
    Write-Step "Creating Web App '$($site.WebAppName)'..."
    $app = New-AzWebApp -ResourceGroupName $rg.Name -Name $site.WebAppName `
        -Location $rg.Location -AppServicePlan $site.PlanName
} else {
    Write-Step "Web App '$($site.WebAppName)' already exists."
}

# --- Optional config ---
if ($site.HttpsOnly) {
    Set-AzWebApp -ResourceGroupName $rg.Name -Name $site.WebAppName -HttpsOnly $true | Out-Null
}

# --- Runtime stack ---
# Windows .NET app; pins the framework so a rebuild restores .NET 10.
if ($site.NetFrameworkVersion) {
    Write-Step "Setting runtime to $($site.NetFrameworkVersion)..."
    Set-AzWebApp -ResourceGroupName $rg.Name -Name $site.WebAppName `
        -NetFrameworkVersion $site.NetFrameworkVersion | Out-Null
}

# --- Custom domain + managed SSL (opt-in) ---
# DNS PREREQUISITE: '$($site.CustomDomain)' must already resolve to this app
# and pass domain verification BEFORE the hostname and certificate can be
# created. If DNS isn't in place yet, run without -WithCustomDomain first,
# point DNS at the app, then re-run with the switch.
if ($WithCustomDomain -and $site.CustomDomain) {
    Write-Step "Binding custom domain '$($site.CustomDomain)'..."

    $app = Get-AzWebApp -ResourceGroupName $rg.Name -Name $site.WebAppName
    if ($app.HostNames -notcontains $site.CustomDomain) {
        Set-AzWebApp -ResourceGroupName $rg.Name -Name $site.WebAppName `
            -HostNames @($site.CustomDomain, $app.DefaultHostName) | Out-Null
    }

    Write-Step "Creating/securing managed certificate for '$($site.CustomDomain)'..."
    $cert = New-AzWebAppCertificate -ResourceGroupName $rg.Name -WebAppName $site.WebAppName `
        -Name "$($site.CustomDomain)-$($site.WebAppName)" -HostName $site.CustomDomain `
        -AddBinding -SslState SniEnabled
}

Write-Host "Done. Site URL: https://$($app.DefaultHostName)" -ForegroundColor Green
if ($WithCustomDomain -and $site.CustomDomain) {
    Write-Host "      Custom domain: https://$($site.CustomDomain)" -ForegroundColor Green
}
