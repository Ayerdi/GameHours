[CmdletBinding()]
param(
    [switch]$Check
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$manifestPath = Join-Path $repoRoot 'src\GameHours.SaveEngine\Cargo.toml'
$outputPath = Join-Path $repoRoot 'src\GameHours.SaveEngine\THIRD-PARTY-RUST-LICENSES.txt'
$targetTriple = 'x86_64-pc-windows-msvc'

function Invoke-CargoText {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    $output = & cargo @Arguments 2>$null
    if ($LASTEXITCODE -ne 0) {
        throw "cargo $($Arguments -join ' ') failed with exit code $LASTEXITCODE"
    }
    return @($output)
}

function Normalize-Text {
    param([Parameter(Mandatory = $true)][string]$Text)
    $normalizedLines = (($Text -replace "`r`n", "`n" -replace "`r", "`n") -split "`n") |
        ForEach-Object { $_.TrimEnd() }
    return (($normalizedLines -join "`n").TrimEnd() + "`n")
}

function Get-TextHash {
    param([Parameter(Mandatory = $true)][string]$Text)

    $bytes = [System.Text.Encoding]::UTF8.GetBytes($Text)
    $hash = [System.Security.Cryptography.SHA256]::HashData($bytes)
    return [Convert]::ToHexString($hash).ToLowerInvariant()
}

$metadataJson = (Invoke-CargoText @(
    'metadata',
    '--manifest-path', $manifestPath,
    '--locked',
    '--format-version', '1'
)) -join "`n"
$metadata = $metadataJson | ConvertFrom-Json -AsHashtable

$tree = Invoke-CargoText @(
    'tree',
    '--manifest-path', $manifestPath,
    '--locked',
    '--target', $targetTriple,
    '-e', 'normal',
    '--prefix', 'none',
    '-f', '{p}'
)

$runtimeKeys = @{}
foreach ($line in $tree) {
    if ($line -match '^([^ ]+) v([^ ]+)') {
        $runtimeKeys["$($Matches[1])@$($Matches[2])"] = $true
    }
}

$packages = @(
    $metadata['packages'] |
        Where-Object {
            $_['name'] -ne 'gamehours-save-engine' -and
            $runtimeKeys.ContainsKey("$($_['name'])@$($_['version'])")
        } |
        Sort-Object { $_['name'] }, { $_['version'] }
)

if ($packages.Count -eq 0) {
    throw 'No third-party Rust packages were resolved for the Windows SaveEngine target.'
}

$canonicalApacheText = $null
foreach ($package in $packages) {
    if ($package['license'] -notmatch 'Apache-2\.0') {
        continue
    }

    $directory = Split-Path -Parent $package['manifest_path']
    $candidate = Get-ChildItem -LiteralPath $directory -File |
        Where-Object { $_.Name -match '^LICENSE[-_.]?APACHE(?:\.|-|_|$)' } |
        Sort-Object Name |
        Select-Object -First 1
    if ($null -ne $candidate) {
        $canonicalApacheText = Normalize-Text ([System.IO.File]::ReadAllText($candidate.FullName))
        break
    }
}

if ([string]::IsNullOrWhiteSpace($canonicalApacheText)) {
    throw 'Could not locate a canonical Apache-2.0 license text in the resolved Cargo packages.'
}

$groups = @{}
$inventory = [System.Collections.Generic.List[string]]::new()

foreach ($package in $packages) {
    $name = [string]$package['name']
    $version = [string]$package['version']
    $license = [string]$package['license']
    $repository = [string]$package['repository']
    $source = [string]$package['source']
    $authors = @($package['authors']) -join '; '
    $directory = Split-Path -Parent $package['manifest_path']
    $licenseFiles = [System.Collections.Generic.List[System.IO.FileInfo]]::new()

    $declaredLicenseFile = [string]$package['license_file']
    if (-not [string]::IsNullOrWhiteSpace($declaredLicenseFile)) {
        $candidate = Join-Path $directory $declaredLicenseFile
        if (Test-Path $candidate -PathType Leaf) {
            $licenseFiles.Add((Get-Item -LiteralPath $candidate))
        }
    }

    if ($licenseFiles.Count -eq 0) {
        foreach ($candidate in @(Get-ChildItem -LiteralPath $directory -File |
            Where-Object { $_.Name -match '^(LICENSE|COPYING|NOTICE)(\.|-|_|$)' } |
            Sort-Object Name)) {
            $licenseFiles.Add($candidate)
        }
    }

    $fallback = $false
    if ($licenseFiles.Count -eq 0) {
        if ($license -notmatch 'Apache-2\.0') {
            throw "$name $version declares '$license' but its package contains no license/notice file and has no Apache-2.0 fallback."
        }
        $fallback = $true
    }

    $inventory.Add("- $name $version")
    $inventory.Add("  Declared license: $license")
    if (-not [string]::IsNullOrWhiteSpace($authors)) { $inventory.Add("  Authors: $authors") }
    if (-not [string]::IsNullOrWhiteSpace($repository)) { $inventory.Add("  Repository: $repository") }
    elseif (-not [string]::IsNullOrWhiteSpace($source)) { $inventory.Add("  Source: $source") }
    if ($fallback) {
        $inventory.Add('  Distribution choice: Apache-2.0 (package metadata offers Apache-2.0; package archive contains no standalone license file)')
    }

    $texts = if ($fallback) {
        @([pscustomobject]@{ Name = 'Apache-2.0 fallback'; Text = $canonicalApacheText })
    }
    else {
        @($licenseFiles | ForEach-Object {
            [pscustomobject]@{
                Name = $_.Name
                Text = Normalize-Text ([System.IO.File]::ReadAllText($_.FullName))
            }
        })
    }

    foreach ($licenseEntry in $texts) {
        $hash = Get-TextHash $licenseEntry.Text
        if (-not $groups.ContainsKey($hash)) {
            $groups[$hash] = [pscustomobject]@{
                Text = $licenseEntry.Text
                Packages = [System.Collections.Generic.List[string]]::new()
            }
        }
        $groups[$hash].Packages.Add("$name $version ($($licenseEntry.Name))")
    }
}

$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add('GameHours SaveEngine - third-party Rust license bundle')
$lines.Add('========================================================')
$lines.Add('')
$lines.Add('This file is generated from Cargo.lock and the dependency packages resolved for:')
$lines.Add("  target: $targetTriple")
$lines.Add("  packages: $($packages.Count)")
$lines.Add('')
$lines.Add('It records package metadata and the license/notice files distributed with each resolved crate.')
$lines.Add('When a package archive omits a standalone license file but its metadata offers Apache-2.0,')
$lines.Add('GameHours selects Apache-2.0 for binary distribution and includes the canonical Apache-2.0 text below.')
$lines.Add('')
$lines.Add('Package inventory')
$lines.Add('-----------------')
foreach ($line in $inventory) { $lines.Add($line) }
$lines.Add('')
$lines.Add('License and notice texts')
$lines.Add('------------------------')

foreach ($entry in @($groups.GetEnumerator() | Sort-Object Name)) {
    $lines.Add('')
    $lines.Add("--- text sha256: $($entry.Key) ---")
    $lines.Add('Used by:')
    foreach ($packageName in @($entry.Value.Packages | Sort-Object -Unique)) {
        $lines.Add("  - $packageName")
    }
    $lines.Add('')
    foreach ($textLine in ($entry.Value.Text -split "`n")) {
        $lines.Add($textLine)
    }
}

$generated = Normalize-Text ($lines -join "`n")

if ($Check) {
    if (-not (Test-Path $outputPath -PathType Leaf)) {
        throw "Generated Rust license bundle is missing: $outputPath"
    }
    $existing = Normalize-Text ([System.IO.File]::ReadAllText($outputPath))
    if ($existing -cne $generated) {
        throw 'Generated Rust license bundle is out of date. Run scripts/generate-save-engine-notices.ps1 and commit the result.'
    }
    Write-Host "SaveEngine Rust license bundle is current ($($packages.Count) packages)."
    exit 0
}

[System.IO.File]::WriteAllText($outputPath, $generated, [System.Text.UTF8Encoding]::new($false))
Write-Host "Generated $outputPath for $($packages.Count) Windows runtime packages."
