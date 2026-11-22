<#
.SYNOPSIS
  Stamps the build version into the app manifest and project, and writes the Esri API key into appsettings.json.
  Used by CI; safe to run locally (the key stays out of git because appsettings.json is restored by `git checkout`).
#>
param(
    [Parameter(Mandatory)] [string] $Version,
    [string] $EsriApiKey = ""
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

# Package.appxmanifest wants Major.Minor.Build.Revision with Revision 0 for the Store.
$parts = $Version.Split('.')
while ($parts.Count -lt 3) { $parts += "0" }
$manifestVersion = "$($parts[0]).$($parts[1]).$($parts[2]).0"
$manifestPath = Join-Path $root "src/Occa.App/Package.appxmanifest"
$manifest = [xml](Get-Content $manifestPath)
$manifest.Package.Identity.Version = $manifestVersion
$manifest.Save($manifestPath)

# Assembly/file version for every project.
$propsPath = Join-Path $root "Directory.Build.props"
(Get-Content $propsPath -Raw) -replace "<Version>[^<]*</Version>", "<Version>$Version</Version>" | Set-Content $propsPath -NoNewline

if ($EsriApiKey) {
    $settingsPath = Join-Path $root "src/Occa.App/appsettings.json"
    $settings = Get-Content $settingsPath -Raw | ConvertFrom-Json
    $settings.Esri.ApiKey = $EsriApiKey
    $settings | ConvertTo-Json -Depth 5 | Set-Content $settingsPath
    Write-Host "Esri API key written to appsettings.json"
} else {
    Write-Host "No Esri API key: the basemap will be unavailable in this build"
}
Write-Host "Stamped version $Version (manifest $manifestVersion)"
