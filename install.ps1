#requires -Version 5.1
$ErrorActionPreference = "Stop"

# PowerShell 5.1 sobre .NET antiguo puede negociar TLS 1.0/1.1 y fallar
# contra GitHub. Forzamos TLS 1.2.
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

# Instalador de un clic generico.
# Descarga la ultima release desde GitHub Releases junto con su checksum
# SHA-256 y ejecuta el instalador real solo tras verificar la integridad.

$Repo = "YOUR-USER/YOUR-REPO"
$ApiUrl = "https://api.github.com/repos/$Repo/releases/latest"
$RepoName = $Repo.Split('/')[-1]
$ZipPattern = "$RepoName-*.zip"
$ChecksumSuffix = ".sha256"

Write-Host ""
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host " $RepoName - instalacion de un clic" -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Buscando la ultima version..." -ForegroundColor Yellow

$release = Invoke-RestMethod -Uri $ApiUrl -Headers @{ "User-Agent" = "$RepoName-installer" }

$zipAssets = @($release.assets | Where-Object { $_.name -like $ZipPattern })
if ($zipAssets.Count -ne 1) {
    throw "Se esperaba exactamente un ZIP del proyecto ($ZipPattern) en la release $($release.tag_name), pero se encontraron $($zipAssets.Count)."
}
$zipAsset = $zipAssets[0]

$checksumName = $zipAsset.name + $ChecksumSuffix
$checksumAsset = $release.assets | Where-Object { $_.name -eq $checksumName } | Select-Object -First 1
if (-not $checksumAsset) {
    throw "La release $($release.tag_name) no publica el checksum $checksumName. No se puede verificar la integridad; se aborta."
}

$ZipPath = Join-Path $env:TEMP $zipAsset.name
$ChecksumPath = Join-Path $env:TEMP $checksumName
$ExtractDir = Join-Path $env:TEMP "$RepoName-extract"

try {
    Write-Host "Version $($release.tag_name)" -ForegroundColor Green
    Write-Host "Descargando $($zipAsset.name) y su checksum..." -ForegroundColor Yellow
    Invoke-WebRequest -UseBasicParsing -Uri $zipAsset.browser_download_url -OutFile $ZipPath
    Invoke-WebRequest -UseBasicParsing -Uri $checksumAsset.browser_download_url -OutFile $ChecksumPath

    Write-Host "Verificando SHA-256..." -ForegroundColor Yellow
    $expectedLine = (Get-Content -Raw $ChecksumPath).Trim()
    $expectedHash = ($expectedLine -split "\s+")[0].ToLowerInvariant()
    if (-not $expectedHash -or $expectedHash -notmatch '^[0-9a-f]{64}$') {
        throw "El checksum descargado no parece un SHA-256 valido."
    }
    $actualHash = (Get-FileHash -Algorithm SHA256 -Path $ZipPath).Hash.ToLowerInvariant()
    if ($actualHash -ne $expectedHash) {
        throw "El SHA-256 del ZIP no coincide con el publicado. Abortando. Esperado=$expectedHash Obtenido=$actualHash"
    }
    Write-Host "      SHA-256 correcto." -ForegroundColor Green

    Write-Host "Extrayendo..." -ForegroundColor Yellow
    if (Test-Path $ExtractDir) { Remove-Item $ExtractDir -Recurse -Force }
    Expand-Archive -Path $ZipPath -DestinationPath $ExtractDir -Force

    $Installer = Get-ChildItem -Path $ExtractDir -Recurse -Filter "*.ps1" |
        Where-Object { $_.Name -match 'Install' } |
        Select-Object -First 1
    if (-not $Installer) {
        throw "No se encontro un instalador (*Install*.ps1) dentro del ZIP."
    }

    & $Installer.FullName
}
finally {
    # Sin basura en %TEMP% si falla cualquiera de los pasos anteriores.
    Remove-Item $ZipPath -Force -ErrorAction SilentlyContinue
    Remove-Item $ChecksumPath -Force -ErrorAction SilentlyContinue
    Remove-Item $ExtractDir -Recurse -Force -ErrorAction SilentlyContinue
}
