[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('stable', 'beta')]
    [string]$Channel,

    [string]$ReleaseDirectory,

    [switch]$RequireDelta,

    [switch]$RequireAuthenticode,

    [string]$ExpectedGithubRepository
)

$ErrorActionPreference = 'Stop'

function Assert-ValidAuthenticodeSignature {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$Label
    )

    $signature = Get-AuthenticodeSignature -FilePath $Path
    if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
        throw "$Label does not have a valid Authenticode signature. Status: $($signature.Status). File: $Path"
    }
}

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

$deltaPackages = @(Get-ChildItem $releaseDir -Filter '*-delta.nupkg' -File)
if ($RequireDelta -and $deltaPackages.Count -eq 0) {
    throw "No delta Velopack package was produced in $releaseDir"
}

$setups = @(Get-ChildItem $releaseDir -Filter '*Setup*.exe' -File)
if ($setups.Count -eq 0) {
    throw "No Velopack Setup executable was produced in $releaseDir"
}

$zeroLength = @(Get-ChildItem $releaseDir -File | Where-Object Length -EQ 0)
if ($zeroLength.Count -gt 0) {
    throw "Velopack output contains empty file(s): $($zeroLength.Name -join ', ')"
}

$latestFullPackage = $fullPackages | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
$extractDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ("GameHours-package-check-" + [Guid]::NewGuid().ToString('N'))

try {
    [System.IO.Directory]::CreateDirectory($extractDirectory) | Out-Null
    [System.IO.Compression.ZipFile]::ExtractToDirectory($latestFullPackage.FullName, $extractDirectory)

    $packagedFiles = @(Get-ChildItem $extractDirectory -File -Recurse)
    $forbiddenNames = @(
        'gamehours.db',
        'gamehours-signing-metadata.json'
    )
    $forbiddenExtensions = @('.pfx', '.p12', '.p8', '.key')
    $sensitiveFiles = @(
        $packagedFiles | Where-Object {
            $forbiddenNames -contains $_.Name.ToLowerInvariant() -or
            $forbiddenExtensions -contains $_.Extension.ToLowerInvariant()
        }
    )
    if ($sensitiveFiles.Count -gt 0) {
        throw "Velopack package contains forbidden user/signing material: $($sensitiveFiles.Name -join ', ')"
    }

    if (-not [string]::IsNullOrWhiteSpace($ExpectedGithubRepository)) {
        $typedSources = @($packagedFiles | Where-Object Name -EQ 'update-source.json')
        $legacySources = @($packagedFiles | Where-Object Name -EQ 'update-source.txt')
        if ($typedSources.Count -ne 1) {
            throw "Expected exactly one update-source.json in full package, found $($typedSources.Count)."
        }
        if ($legacySources.Count -ne 0) {
            throw 'GitHub release package must not also contain legacy update-source.txt.'
        }

        try {
            $sourceDocument = Get-Content $typedSources[0].FullName -Raw | ConvertFrom-Json
        }
        catch {
            throw "Packaged update-source.json is invalid JSON. $($_.Exception.Message)"
        }

        if ($sourceDocument.type -ne 'github' -or
            $sourceDocument.repository -ne $ExpectedGithubRepository) {
            throw "Packaged GitHub update source does not match expected repository '$ExpectedGithubRepository'."
        }
    }

    if ($RequireAuthenticode) {
        $latestSetup = $setups | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
        Assert-ValidAuthenticodeSignature -Path $latestSetup.FullName -Label 'Velopack Setup'

        $gameHoursExecutables = @($packagedFiles | Where-Object { $_.Name -like 'GameHours*.exe' })
        $mainExecutable = @($gameHoursExecutables | Where-Object Name -EQ 'GameHours.Desktop.exe')
        if ($mainExecutable.Count -ne 1) {
            throw "Expected exactly one GameHours.Desktop.exe in full Velopack package, found $($mainExecutable.Count)."
        }

        foreach ($executable in $gameHoursExecutables) {
            Assert-ValidAuthenticodeSignature -Path $executable.FullName -Label $executable.Name
        }
    }
}
finally {
    if (Test-Path $extractDirectory) {
        Remove-Item $extractDirectory -Recurse -Force
    }
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
Write-Host "  Delta:     $($deltaPackages.Count)"
Write-Host "  Setup:     $($setups.Count)"
Write-Host "  Signed:    $RequireAuthenticode"
Write-Host "  Checksums: $checksumPath"
