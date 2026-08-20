#requires -Version 5.1
$ErrorActionPreference = 'Stop'

# Marca este repositorio como template de GitHub (is_template = true).
# GitHub no lo permite al crear el repo; se hace con la API despues.
# Uso (desde un repo creado con "Use this template"):
#   powershell -File Set-RepoTemplate.ps1

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    throw 'No se encontro el CLI gh. Instalalo y autenticate: https://cli.github.com'
}

$remote = git config --get remote.origin.url
if (-not $remote) {
    throw 'No hay remote origin. Anade el remote antes de marcar el repo como template.'
}
if ($remote -notmatch 'github\.com[/:]([^/]+)/([^/\.]+)') {
    throw "No se pudo deducir OWNER/REPO del origin: $remote"
}
$repo = "{0}/{1}" -f $Matches[1], $Matches[2]

$result = gh api "repos/$repo" -X PATCH -f is_template=true --jq '.is_template'
if ($result -eq 'true') {
    Write-Host "OK: repos/$repo marcado como template."
}
else {
    throw "No se pudo marcar repos/$repo como template."
}
