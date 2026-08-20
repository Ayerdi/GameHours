[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+([+-][0-9A-Za-z.-]+)?$')]
    [string]$Version,

    [ValidateSet('stable', 'beta')]
    [string]$Channel = 'beta',

    [string]$ReleaseNotes
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $repoRoot 'artifacts\publish\win-x64'
$releaseDir = Join-Path $repoRoot "artifacts\velopack\$Channel"
$project = Join-Path $repoRoot 'src\GameHours.App\GameHours.App.csproj'

if (Test-Path $publishDir) {
    Remove-Item $publishDir -Recurse -Force
}

New-Item -ItemType Directory -Path $publishDir -Force | Out-Null
New-Item -ItemType Directory -Path $releaseDir -Force | Out-Null

Push-Location $repoRoot
try {
    Write-Host "Restoring pinned .NET tools..."
    dotnet tool restore
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet tool restore failed with exit code $LASTEXITCODE"
    }

    Write-Host "Publishing GameHours $Version (win-x64, self-contained)..."
    dotnet publish $project `
        -c Release `
        -r win-x64 `
        --self-contained true `
        -o $publishDir `
        "/p:Version=$Version"
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE"
    }

    $vpkArgs = @(
        'vpk', 'pack',
        '--packId', 'Ayerdi.GameHours',
        '--packVersion', $Version,
        '--packDir', $publishDir,
        '--mainExe', 'GameHours.App.exe',
        '--packTitle', 'GameHours',
        '--packAuthors', 'Ayerdi',
        '--runtime', 'win-x64',
        '--channel', $Channel,
        '--outputDir', $releaseDir
    )

    if (-not [string]::IsNullOrWhiteSpace($ReleaseNotes)) {
        $releaseNotesPath = Resolve-Path $ReleaseNotes
        $vpkArgs += @('--releaseNotes', $releaseNotesPath.Path)
    }

    Write-Host "Packaging Velopack release into $releaseDir..."
    dotnet @vpkArgs
    if ($LASTEXITCODE -ne 0) {
        throw "vpk pack failed with exit code $LASTEXITCODE"
    }

    $setup = Get-ChildItem $releaseDir -Filter '*Setup*.exe' -File |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1

    Write-Host ''
    Write-Host "GameHours $Version ($Channel) packaged successfully."
    Write-Host "Release feed: $releaseDir"
    if ($null -ne $setup) {
        Write-Host "Installer:    $($setup.FullName)"
    }
    Write-Host ''
    Write-Host 'Keep this release directory between versions so Velopack can generate delta packages.'
}
finally {
    Pop-Location
}
