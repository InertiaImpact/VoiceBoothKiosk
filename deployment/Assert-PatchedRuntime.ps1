[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$PublishRoot)

$ErrorActionPreference = 'Stop'
foreach ($relative in @('recorder\VoiceBoothKiosk', 'config\VoiceBoothKiosk.Config')) {
    $basePath = Join-Path $PublishRoot $relative
    $configuration = Get-Content -LiteralPath "$basePath.runtimeconfig.json" -Raw | ConvertFrom-Json
    foreach ($name in @('Microsoft.NETCore.App', 'Microsoft.WindowsDesktop.App')) {
        $framework = @($configuration.runtimeOptions.includedFrameworks | Where-Object name -EQ $name)
        if ($framework.Count -ne 1 -or [version]$framework[0].version -lt [version]'10.0.14') {
            throw "$relative must bundle $name 10.0.14 or later (CVE-2026-111497)."
        }
    }
    $dependencies = Get-Content -LiteralPath "$basePath.deps.json" -Raw | ConvertFrom-Json
    $runtimePacks = @($dependencies.libraries.PSObject.Properties.Name | Where-Object { $_ -like 'runtimepack.Microsoft.NETCore.App.Runtime.win-x64/*' })
    if ($runtimePacks.Count -ne 1 -or [version]($runtimePacks[0].Split('/')[-1]) -lt [version]'10.0.14') {
        throw "$relative dependency manifest does not contain a patched Windows runtime pack."
    }
}
Write-Host 'Bundled runtime security check passed (minimum 10.0.14).'
