[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('stable', 'beta')]
    [string]$Channel,

    [string]$ReleaseDirectory
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($ReleaseDirectory)) {
    $ReleaseDirectory = Join-Path $repoRoot "artifacts\velopack\$Channel"
}

$releaseDir = [System.IO.Path]::GetFullPath($ReleaseDirectory)
if (-not (Test-Path $releaseDir -PathType Container)) {
    throw "Velopack release directory does not exist: $releaseDir"
}

$releaseIndex = Join-Path $releaseDir "releases.$Channel.json"
if (-not (Test-Path $releaseIndex -PathType Leaf)) {
    throw "Missing Velopack release index: $releaseIndex"
}

try {
    $index = Get-Content $releaseIndex -Raw | ConvertFrom-Json
}
catch {
    throw "Velopack release index is not valid JSON: $releaseIndex. $($_.Exception.Message)"
}

if ($null -eq $index) {
    throw "Velopack release index is empty: $releaseIndex"
}

$fullPackages = @(Get-ChildItem $releaseDir -Filter '*-full.nupkg' -File)
if ($fullPackages.Count -eq 0) {
    throw "No full Velopack package was produced in $releaseDir"
}

$setups = @(Get-ChildItem $releaseDir -Filter '*Setup*.exe' -File)
if ($setups.Count -eq 0) {
    throw "No Velopack Setup executable was produced in $releaseDir"
}

$zeroLength = @(Get-ChildItem $releaseDir -File | Where-Object Length -EQ 0)
if ($zeroLength.Count -gt 0) {
    throw "Velopack output contains empty file(s): $($zeroLength.Name -join ', ')"
}

$checksumPath = Join-Path $releaseDir 'SHA256SUMS.txt'
$artifactsToHash = @(
    Get-ChildItem $releaseDir -File |
        Where-Object Name -NE 'SHA256SUMS.txt' |
        Sort-Object Name
)

$checksumLines = foreach ($artifact in $artifactsToHash) {
    $hash = (Get-FileHash $artifact.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $($artifact.Name)"
}
[System.IO.File]::WriteAllLines(
    $checksumPath,
    $checksumLines,
    [System.Text.UTF8Encoding]::new($false))

Write-Host "Validated Velopack $Channel release:"
Write-Host "  Directory: $releaseDir"
Write-Host "  Index:     $([System.IO.Path]::GetFileName($releaseIndex))"
Write-Host "  Full:      $($fullPackages.Count)"
Write-Host "  Setup:     $($setups.Count)"
Write-Host "  Checksums: $checksumPath"
