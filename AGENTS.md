# AGENTS.md — maintaining <REPO_NAME>

> Escribe esto ANTES de empezar a codificar. Es el contrato entre humanos y
> agentes que trabaje en este repo. Mantenlo al dia: si una convencion cambia,
> actualizalo en el mismo PR.

## Goal

<Que hace este proyecto, en 2-3 frases. Para quien y por que existe.>

## Quality bar (no negociable)

Estos criterios son la razon de ser de esta plantilla. No los elimines sin
una justificacion explicita y documentada.

1. **CI verde o no se toca nada.** `validate` corre en cada push/PR: sintaxis,
   PSScriptAnalyzer, ficheros requeridos y Pester. Un cambio que rompa CI no
   se mergea.
2. **Logica pura en `lib/`, testeada.** Todo lo que pueda fallar sin entorno
   real vive en `lib/Core.psm1` (o modulos propios) y tiene tests Pester en
   `tests/`. Los scripts de la raiz son delgados: I/O, bucle, glue.
3. **Descargas externas verificadas por hash.** Cualquier binario/archivo que
   baje el instalador debe tener un SHA-256 pinneado y comprobarse antes de
   ejecutarse. Nunca desactives la verificacion: actualiza el hash con fecha y
   fuente.
4. **Nada sin timeout.** Toda operacion que pueda colgarse (red, WebSocket,
   procesos) tiene un timeout acotado con `New-TimeoutToken`. Cero hangs.
5. **Sin valores magicos hardcodeados.** Config por fichero (`config.json`),
   nunca por variable en el script.
6. **Errores honestos.** Si algo falla, el log dice QUE fallo y POR QUE, no un
   mensaje generico. No falles en silencio; no menciones un componente cuando
   el fallo es de otro.
7. **Sin secretos ni datos de maquina.** Nada de credenciales, rutas absolutas
   de una maquina concreta ni IDs especificos de hardware en el repo.
8. **Cleanup garantizado.** Cualquier recurso temporal se limpia en `finally`,
   aunque falle el paso anterior.

## Commands

- Test: `Invoke-Pester tests`
- Lint: `Invoke-ScriptAnalyzer -Path . -Recurse -Severity Error, Warning`
- Release: tag `vX.Y.Z` en main (el workflow `release` empaqueta ZIP + `.sha256`)

## Conventions

- PowerShell 5.1 como minimo; `#requires -Version 5.1` en cada script.
- `$ErrorActionPreference = 'Stop'` en scripts ejecutables.
- Nombre de ficheros: <convencion del proyecto>.
- Line endings: CRLF en `.ps1`, LF en lo demas (`.gitattributes`).

## Verified design state

<Hechos MEDIDOS, no supuestos: comportamiento observado, puertos, formatos de
respuesta, versiones. Nunca "deberia funcionar" — solo lo que se verifico.
Anota fecha.>

## Do not assume

- <Que NO debes dar por hecho: APIs no oficiales, IDs estables, nombres
  siempre iguales, compatibilidad entre dispositivos/plataformas.>

## Required tests after any change

<Checklist de pruebas MANUALES que un cambio debe pasar antes de mergear.>

## Possible future improvements

<Ideas, sin implementar a no ser que se pidan.>
