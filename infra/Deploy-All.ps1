<#
.SYNOPSIS
    Runs every resource deploy script. Add new ones to the list below as you
    bring more of your environment under script control.

.EXAMPLE
    .\Deploy-All.ps1
.EXAMPLE
    .\Deploy-All.ps1 -Rebuild
#>
[CmdletBinding()]
param([switch]$Rebuild)

$scripts = @(
    'Deploy-AtlasDatabase.ps1',     # database first - the site depends on it
    'Deploy-CorporateWebsite.ps1'
)

foreach ($script in $scripts) {
    & "$PSScriptRoot\$script" -Rebuild:$Rebuild
}
