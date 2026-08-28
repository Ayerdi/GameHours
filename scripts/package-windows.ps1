[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$')]
    [string]$Version,

    [ValidateSet('stable', 'beta')]
    [string]$Channel = 'beta',

    [string]$ReleaseNotes,

    [string]$UpdateSource,

    [string]$AzureTrustedSignFile
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $repoRoot 'artifacts\publish\win-x64'
$releaseDir = Join-Path $repoRoot "artifacts\velopack\$Channel"
$project = Join-Path $repoRoot 'src\GameHours.Desktop\GameHours.Desktop.csproj'
$validator = Join-Path $PSScriptRoot 'validate-velopack-release.ps1'

if (-not [string]::IsNullOrWhiteSpace($UpdateSource)) {
    $trimmedUpdateSource = $UpdateSource.Trim()
    $updateUri = $null
    $isHttps = [Uri]::TryCreate($trimmedUpdateSource, [UriKind]::Absolute, [ref]$updateUri) -and
        $updateUri.Scheme -eq [Uri]::UriSchemeHttps -and
        -not [string]::IsNullOrWhiteSpace($updateUri.Host) -and
        [string]::IsNullOrEmpty($updateUri.UserInfo) -and
        [string]::IsNullOrEmpty($updateUri.Query) -and
        [string]::IsNullOrEmpty($updateUri.Fragment)

    if (-not $isHttps) {
        throw 'Embedded UpdateSource must be an absolute HTTPS URL without credentials, query string, or fragment. Use GAMEHOURS_UPDATE_SOURCE for an explicit local test feed.'
    }
}

$azureSigningMetadataPath = $null
if (-not [string]::IsNullOrWhiteSpace($AzureTrustedSignFile)) {
    if (-not (Test-Path $AzureTrustedSignFile -PathType Leaf)) {
        throw "Azure Artifact Signing metadata file does not exist: $AzureTrustedSignFile"
    }

    $azureSigningMetadataPath = (Resolve-Path $AzureTrustedSignFile).Path
    try {
        $azureSigningMetadata = Get-Content $azureSigningMetadataPath -Raw | ConvertFrom-Json
    }
    catch {
        throw "Azure Artifact Signing metadata is not valid JSON: $azureSigningMetadataPath. $($_.Exception.Message)"
    }

    foreach ($propertyName in @('Endpoint', 'CodeSigningAccountName', 'CertificateProfileName')) {
        $property = $azureSigningMetadata.PSObject.Properties[$propertyName]
        if ($null -eq $property -or [string]::IsNullOrWhiteSpace([string]$property.Value)) {
            throw "Azure Artifact Signing metadata is missing required property '$propertyName'."
        }
    }

    $signingEndpoint = [string]$azureSigningMetadata.Endpoint
    $signingUri = $null
    $validSigningEndpoint = [Uri]::TryCreate($signingEndpoint, [UriKind]::Absolute, [ref]$signingUri) -and
        $signingUri.Scheme -eq [Uri]::UriSchemeHttps -and
        -not [string]::IsNullOrWhiteSpace($signingUri.Host) -and
        [string]::IsNullOrEmpty($signingUri.UserInfo) -and
        [string]::IsNullOrEmpty($signingUri.Query) -and
        [string]::IsNullOrEmpty($signingUri.Fragment)

    if (-not $validSigningEndpoint) {
        throw 'Azure Artifact Signing Endpoint must be an absolute HTTPS URL without credentials, query string, or fragment.'
    }
}

if (Test-Path $publishDir) {
    Remove-Item $publishDir -Recurse -Force
}

New-Item -ItemType Directory -Path $publishDir -Force | Out-Null
New-Item -ItemType Directory -Path $releaseDir -Force | Out-Null

Push-Location $repoRoot
try {
    Write-Host "Restoring locked Desktop dependencies..."
    dotnet restore $project --locked-mode
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet restore --locked-mode failed with exit code $LASTEXITCODE"
    }

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
        --no-restore `
        -o $publishDir `
        "/p:Version=$Version"
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE"
    }

    if (-not [string]::IsNullOrWhiteSpace($UpdateSource)) {
        $sourcePath = Join-Path $publishDir 'update-source.txt'
        [System.IO.File]::WriteAllText(
            $sourcePath,
            $trimmedUpdateSource,
            [System.Text.UTF8Encoding]::new($false))
        Write-Host "Embedded HTTPS update source configuration: $trimmedUpdateSource"
    }

    $releaseNotesPath = $null
    if (-not [string]::IsNullOrWhiteSpace($ReleaseNotes)) {
        $releaseNotesPath = Resolve-Path $ReleaseNotes
        Copy-Item $releaseNotesPath.Path (Join-Path $publishDir 'release-notes.md') -Force
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

    if ($null -ne $releaseNotesPath) {
        $vpkArgs += @('--releaseNotes', $releaseNotesPath.Path)
    }

    if ($null -ne $azureSigningMetadataPath) {
        $vpkArgs += @('--azureTrustedSignFile', $azureSigningMetadataPath)
        Write-Host 'Azure Artifact Signing enabled for this package.'
    }

    Write-Host "Packaging Velopack release into $releaseDir..."
    dotnet @vpkArgs
    if ($LASTEXITCODE -ne 0) {
        throw "vpk pack failed with exit code $LASTEXITCODE"
    }

    Write-Host "Validating Velopack output..."
    $validationArguments = @{
        Channel = $Channel
        ReleaseDirectory = $releaseDir
    }
    if ($null -ne $azureSigningMetadataPath) {
        $validationArguments.RequireAuthenticode = $true
    }
    & $validator @validationArguments

    $setup = Get-ChildItem $releaseDir -Filter '*Setup*.exe' -File |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1

    Write-Host ''
    Write-Host "GameHours $Version ($Channel) packaged and validated successfully."
    Write-Host "Release feed: $releaseDir"
    if ($null -ne $setup) {
        Write-Host "Installer:    $($setup.FullName)"
    }
    Write-Host "Checksums:   $(Join-Path $releaseDir 'SHA256SUMS.txt')"
    Write-Host ''
    if ([string]::IsNullOrWhiteSpace($UpdateSource)) {
        Write-Host 'No update source was embedded. The installed desktop can still use GAMEHOURS_UPDATE_SOURCE.'
    }
    Write-Host 'Keep this release directory between versions so Velopack can generate delta packages.'
}
finally {
    Pop-Location
}
