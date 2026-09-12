[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$')]
    [string]$Version,

    [ValidateSet('stable', 'beta')]
    [string]$Channel = 'beta',

    [string]$ReleaseNotes,

    [string]$UpdateSource,

    [string]$GithubUpdateRepository,

    [string]$AzureTrustedSignFile,

    [switch]$RequireDelta
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $repoRoot 'artifacts\publish\win-x64'
$releaseDir = Join-Path $repoRoot "artifacts\velopack\$Channel"
$project = Join-Path $repoRoot 'src\GameHours.Desktop\GameHours.Desktop.csproj'
$saveEngineManifest = Join-Path $repoRoot 'src\GameHours.SaveEngine\Cargo.toml'
$saveManifestSha256 = '87E69A3CE52F1170FF35FEDA47D0041E5BE21E4195478722684C8E119469896D'
$saveEngineNoticeGenerator = Join-Path $PSScriptRoot 'generate-save-engine-notices.ps1'
$saveManifestGenerator = Join-Path $PSScriptRoot 'generate-save-manifest.ps1'
$validator = Join-Path $PSScriptRoot 'validate-velopack-release.ps1'

if (-not [string]::IsNullOrWhiteSpace($UpdateSource) -and
    -not [string]::IsNullOrWhiteSpace($GithubUpdateRepository)) {
    throw 'UpdateSource and GithubUpdateRepository are mutually exclusive.'
}

$trimmedUpdateSource = $null
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

$trimmedGithubUpdateRepository = $null
if (-not [string]::IsNullOrWhiteSpace($GithubUpdateRepository)) {
    $githubUri = $null
    $candidate = $GithubUpdateRepository.Trim()
    $validGithubRepository = [Uri]::TryCreate($candidate, [UriKind]::Absolute, [ref]$githubUri) -and
        $githubUri.Scheme -eq [Uri]::UriSchemeHttps -and
        $githubUri.Host -eq 'github.com' -and
        $githubUri.IsDefaultPort -and
        [string]::IsNullOrEmpty($githubUri.UserInfo) -and
        [string]::IsNullOrEmpty($githubUri.Query) -and
        [string]::IsNullOrEmpty($githubUri.Fragment)

    $segments = if ($null -ne $githubUri) {
        @($githubUri.AbsolutePath.Split('/', [StringSplitOptions]::RemoveEmptyEntries))
    } else {
        @()
    }
    if (-not $validGithubRepository -or $segments.Count -ne 2) {
        throw 'GithubUpdateRepository must be an HTTPS github.com owner/repository URL.'
    }

    $repositoryName = $segments[1]
    if ($repositoryName.EndsWith('.git', [StringComparison]::OrdinalIgnoreCase)) {
        $repositoryName = $repositoryName.Substring(0, $repositoryName.Length - 4)
    }
    if ([string]::IsNullOrWhiteSpace($segments[0]) -or [string]::IsNullOrWhiteSpace($repositoryName)) {
        throw 'GithubUpdateRepository must include both owner and repository.'
    }

    $trimmedGithubUpdateRepository = "https://github.com/$($segments[0])/$repositoryName"
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
    Write-Host "Building pinned GameHours SaveEngine bridge..."
    cargo build --manifest-path $saveEngineManifest --release --locked
    if ($LASTEXITCODE -ne 0) {
        throw "cargo build --release --locked failed with exit code $LASTEXITCODE"
    }
    & $saveEngineNoticeGenerator -Check
    if ($LASTEXITCODE -ne 0) {
        throw "SaveEngine license verification failed with exit code $LASTEXITCODE"
    }
    & $saveManifestGenerator -Check
    if ($LASTEXITCODE -ne 0) {
        throw "Save manifest verification failed with exit code $LASTEXITCODE"
    }

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

    $saveEnginePath = Join-Path $publishDir 'tools\GameHours.SaveEngine.exe'
    if (-not (Test-Path $saveEnginePath -PathType Leaf)) {
        throw "Published SaveEngine helper is missing: $saveEnginePath"
    }

    $thirdPartyNoticesPath = Join-Path $publishDir 'THIRD-PARTY-NOTICES.md'
    if (-not (Test-Path $thirdPartyNoticesPath -PathType Leaf)) {
        throw "Published third-party notices are missing: $thirdPartyNoticesPath"
    }

    $rustLicenseBundlePath = Join-Path $publishDir 'THIRD-PARTY-RUST-LICENSES.txt'
    if (-not (Test-Path $rustLicenseBundlePath -PathType Leaf)) {
        throw "Published Rust third-party license bundle is missing: $rustLicenseBundlePath"
    }

    $saveManifestPath = Join-Path $publishDir 'tools\ludusavi-manifest.yaml'
    if (-not (Test-Path $saveManifestPath -PathType Leaf)) {
        throw "Published Ludusavi manifest is missing: $saveManifestPath"
    }
    $saveManifestHash = (Get-FileHash $saveManifestPath -Algorithm SHA256).Hash
    if ($saveManifestHash -ne $saveManifestSha256) {
        throw "Published Ludusavi manifest hash mismatch: $saveManifestHash"
    }

    $capabilityRequest = '{"protocolVersion":2,"requestId":"package-capabilities","operation":"getCapabilities","payload":{}}'
    try {
        $capabilities = $capabilityRequest | & $saveEnginePath | ConvertFrom-Json
    }
    catch {
        throw "Published SaveEngine helper failed its capability smoke: $($_.Exception.Message)"
    }
    if (-not $capabilities.ok -or
        $capabilities.result.ludusaviRevision -ne '8844d7b67e784909f4ef42f7bfb047b700fe7b15' -or
        $capabilities.result.operations -notcontains 'createGameBackup' -or
        $capabilities.result.dataScopes -notcontains 'portableSave') {
        throw 'Published SaveEngine helper does not report the expected pin/capabilities.'
    }

    if ($null -ne $trimmedUpdateSource) {
        $sourcePath = Join-Path $publishDir 'update-source.txt'
        [System.IO.File]::WriteAllText(
            $sourcePath,
            $trimmedUpdateSource,
            [System.Text.UTF8Encoding]::new($false))
        Write-Host "Embedded HTTPS update source configuration: $trimmedUpdateSource"
    }

    if ($null -ne $trimmedGithubUpdateRepository) {
        $sourcePath = Join-Path $publishDir 'update-source.json'
        $sourceDocument = [ordered]@{
            type = 'github'
            repository = $trimmedGithubUpdateRepository
        } | ConvertTo-Json -Compress
        [System.IO.File]::WriteAllText(
            $sourcePath,
            $sourceDocument,
            [System.Text.UTF8Encoding]::new($false))
        Write-Host "Embedded GitHub Releases update source: $trimmedGithubUpdateRepository"
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
    if ($RequireDelta) {
        $validationArguments.RequireDelta = $true
    }
    if ($null -ne $azureSigningMetadataPath) {
        $validationArguments.RequireAuthenticode = $true
    }
    if ($null -ne $trimmedUpdateSource) {
        $validationArguments.ExpectedUpdateSource = $trimmedUpdateSource
    }
    elseif ($null -ne $trimmedGithubUpdateRepository) {
        $validationArguments.ExpectedGithubUpdateRepository = $trimmedGithubUpdateRepository
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
    if ($null -eq $trimmedUpdateSource -and $null -eq $trimmedGithubUpdateRepository) {
        Write-Host 'No update source was embedded. The installed desktop can still use GAMEHOURS_UPDATE_SOURCE.'
    }
    Write-Host 'Keep this release directory between versions so Velopack can generate delta packages.'
}
finally {
    Pop-Location
}
