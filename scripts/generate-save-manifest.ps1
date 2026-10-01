[CmdletBinding()]
param(
    [string]$SourcePath,
    [switch]$Check
)

$ErrorActionPreference = 'Stop'

$upstreamSha256 = '193A7B47A9DD80E9CA1C239D7CF78B50720902D4B0F9BC38D23949715E4B77BF'
$distributedSha256 = '87E69A3CE52F1170FF35FEDA47D0041E5BE21E4195478722684C8E119469896D'
$repoRoot = Split-Path -Parent $PSScriptRoot
$outputPath = Join-Path $repoRoot 'src\GameHours.SaveEngine\manifest\manifest.yaml'

if ($Check) {
    if (-not (Test-Path $outputPath -PathType Leaf)) {
        throw "Sanitized Ludusavi manifest is missing: $outputPath"
    }

    $actual = (Get-FileHash $outputPath -Algorithm SHA256).Hash
    if ($actual -ne $distributedSha256) {
        throw "Sanitized Ludusavi manifest is stale or modified. Expected $distributedSha256, found $actual."
    }

    Write-Host 'Sanitized Ludusavi manifest is current.'
    return
}

if ([string]::IsNullOrWhiteSpace($SourcePath) -or -not (Test-Path $SourcePath -PathType Leaf)) {
    throw 'SourcePath must point to the pinned upstream data/manifest.yaml.'
}

$resolvedSource = (Resolve-Path $SourcePath).Path
$sourceHash = (Get-FileHash $resolvedSource -Algorithm SHA256).Hash
if ($sourceHash -ne $upstreamSha256) {
    throw "Unexpected upstream manifest hash. Expected $upstreamSha256, found $sourceHash."
}

$outputDirectory = Split-Path -Parent $outputPath
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$temporaryPath = "$outputPath.tmp"

$reader = [System.IO.File]::OpenText($resolvedSource)
$writer = [System.IO.StreamWriter]::new($temporaryPath, $false, [System.Text.UTF8Encoding]::new($false))
try {
    $includeBlock = $true
    while (($line = $reader.ReadLine()) -ne $null) {
        if ($line -match '^  ([A-Za-z][A-Za-z0-9]*):') {
            # Ludusavi 0.31 save scanning does not consume launch metadata. Some upstream launch
            # entries contain historical launcher credentials, so GameHours deliberately strips
            # every launch block instead of distributing those values.
            $includeBlock = $Matches[1] -ne 'launch'
        }
        elseif ($line -match '^\S') {
            $includeBlock = $true
        }

        if ($includeBlock) {
            $writer.WriteLine($line)
        }
    }
}
finally {
    $reader.Dispose()
    $writer.Dispose()
}

$generatedHash = (Get-FileHash $temporaryPath -Algorithm SHA256).Hash
if ($generatedHash -ne $distributedSha256) {
    Remove-Item -LiteralPath $temporaryPath -Force
    throw "Generated manifest hash mismatch. Expected $distributedSha256, found $generatedHash."
}

Move-Item -LiteralPath $temporaryPath -Destination $outputPath -Force
Write-Host "Generated sanitized Ludusavi manifest: $distributedSha256"
