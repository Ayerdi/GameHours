# <REPO_NAME>

<Una frase: que hace. Idealmente una demo/GIF al lado de la intro.>

## Quality bar

Este repo nace de una plantilla cuyo objetivo es que CADA proyecto mantenga un
nivel minimo de calidad. La barra es esta:

| Criterio | Como se cumple |
|----------|----------------|
| CI en verde siempre | `validate` (sintaxis + PSScriptAnalyzer + Pester) en cada push/PR |
| Logica testeada | Modulos puros en `lib/` con tests en `tests/` |
| Descargas verificadas | Instalador comprueba SHA-256 antes de ejecutar nada |
| Sin hangs | Timeouts acotados en toda operacion de red/proceso |
| Config externa | `config.json`, nunca valores hardcodeados |
| Errores honestos | Logs que dicen que fallo y por que |
| Sin secretos | Nada de credenciales ni datos de maquina en el repo |
| Cleanup garantizado | Recursos temporales limpiados en `finally` |

No relajes estos criterios para ir "mas rapido": son la parte barata de la
calidad y la que evita las 3am.

## Installation

Instalador de un clic: descarga la ultima release, verifica su SHA-256 contra
el checksum publicado y solo entonces instala.

```powershell
powershell -ExecutionPolicy Bypass -File install.ps1
```

## Usage

<Como se usa el proyecto.>

## Configuration

<Que parametriza `config.json`.>

## Development

```powershell
# Tests (Pester)
Invoke-Pester tests

# Lint (PSScriptAnalyzer)
Invoke-ScriptAnalyzer -Path . -Recurse -Severity Error, Warning
```

Ver `AGENTS.md` para el contrato completo de mantenimiento.

## Template: self-propagating

Un repo creado desde esta plantilla **no** es template por defecto. Para
marcarlo como template (para poder "Use this template" sobre el):

```powershell
powershell -File Set-RepoTemplate.ps1
```

## Releasing

Pulsa un tag `vX.Y.Z` en main. El workflow `release` genera el ZIP y su
`.sha256` y los sube como assets de la release. Verifica el checksum:

```bash
sha256sum <repo>-vX.Y.Z.zip
cat <repo>-vX.Y.Z.zip.sha256
```

## License

MIT — ver `LICENSE`.
