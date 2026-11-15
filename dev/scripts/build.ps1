#Requires -Version 7
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [switch]$Offline
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
Push-Location -LiteralPath $root
try {
    $restore = @()
    if ($Offline) {
        $packageCache = $env:NUGET_PACKAGES
        if (!$packageCache) { $packageCache = Join-Path $env:USERPROFILE '.nuget/packages' }
        $restore = @("-p:RestoreSources=$packageCache", '-p:NuGetAudit=false')
    }
    $common = @('-c', $Configuration, '-m:1', '-p:UseSharedCompilation=false') + $restore
    $app = 'src/EchoType.App'
    $version = ([xml](Get-Content -LiteralPath Directory.Build.props -Raw)).Project.PropertyGroup.Version
    $output = if ($Configuration -eq 'Release') { "release/v$version" } else { 'release/Debug' }
    dotnet publish $app @common --no-self-contained -o $output
}
finally { Pop-Location }
