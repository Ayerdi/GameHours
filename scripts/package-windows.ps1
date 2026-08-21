[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+([+-][0-9A-Za-z.-]+)?$')]
    [string]$Version,

    [ValidateSet('stable', 'beta')]
    [string]$Channel = 'beta',

    [string]$ReleaseNotes,

    [string]$UpdateSource
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $repoRoot 'artifacts\publish\win-x64'
$releaseDir = Join-Path $repoRoot "artifacts\velopack\$Channel"
$project = Join-Path $repoRoot 'src\GameHours.Desktop\GameHours.Desktop.csproj'

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

    Write-Host "Publishing GameHours Desktop $Version (win-x64, self-contained)..."
    dotnet publish $project `
        -c Release `
        -r win-x64 `
        --self-contained true `
        -o $publishDir `
        "/p:Version=$Version"
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE"
    }

    if (-not [string]::IsNullOrWhiteSpace($UpdateSource)) {
        $sourcePath = Join-Path $publishDir 'update-source.txt'
        [System.IO.File]::WriteAllText(
            $sourcePath,
            $UpdateSource.Trim(),
            [System.Text.UTF8Encoding]::new($false))
        Write-Host "Embedded update source configuration: $($UpdateSource.Trim())"
    }

    $vpkArgs = @(
        'vpk', 'pack',
        '--packId', 'Ayerdi.GameHours',
        '--packVersion', $Version,
        '--packDir', $publishDir,
        '--mainExe', 'GameHours.Desktop.exe',
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
    if ([string]::IsNullOrWhiteSpace($UpdateSource)) {
        Write-Host 'No update source was embedded. The installed desktop can still use GAMEHOURS_UPDATE_SOURCE.'
    }
    Write-Host 'Keep this release directory between versions so Velopack can generate delta packages.'
}
finally {
    Pop-Location
}
