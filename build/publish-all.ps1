<#
.SYNOPSIS
    Publishes the client for every supported platform.

.DESCRIPTION
    Every target is published self-contained, so the user installs nothing: the
    .NET runtime travels inside the binary. With PublishSingleFile the result
    is a single executable per platform.

.EXAMPLE
    ./build/publish-all.ps1
    ./build/publish-all.ps1 -Configuration Release
#>
[CmdletBinding()]
param(
    [string] $Configuration = "Release",
    [string] $OutputRoot = "artifacts"
)

$ErrorActionPreference = "Stop"
$project = Join-Path $PSScriptRoot "..\src\FesticalDoomsday.Client\FesticalDoomsday.Client.csproj"
$targets = @(
    @{ Rid = "win-x64";   Folder = "windows-x64";   Archive = "FesticalDoomsdayClient-windows-x64.zip" },
    @{ Rid = "linux-x64"; Folder = "linux-x64";     Archive = "FesticalDoomsdayClient-linux-x64.tar.gz" },
    @{ Rid = "osx-arm64"; Folder = "macos-arm64";   Archive = "FesticalDoomsdayClient-macos-arm64.tar.gz" },
    @{ Rid = "osx-x64";   Folder = "macos-x64";     Archive = "FesticalDoomsdayClient-macos-x64.tar.gz" }
)

$root = Join-Path $PSScriptRoot "..\$OutputRoot"
New-Item -ItemType Directory -Force -Path $root | Out-Null

foreach ($target in $targets) {
    $out = Join-Path $root $target.Folder
    Write-Host "==> $($target.Rid)" -ForegroundColor Cyan
    dotnet publish $project `
        --configuration $Configuration `
        --runtime $target.Rid `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=none `
        -p:PublishTrimmed=false `
        --output $out
    if ($LASTEXITCODE -ne 0) { throw "publish failed for $($target.Rid)" }
}

Write-Host ""
Write-Host "Published to $root" -ForegroundColor Green
Get-ChildItem -Path $root -Recurse -File |
    Where-Object { $_.Extension -in ".exe", "" -and $_.Name -like "FesticalDoomsdayClient*" } |
    Select-Object FullName, @{ Name = "MB"; Expression = { [math]::Round($_.Length / 1MB, 1) } }
