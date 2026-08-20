#requires -Version 5.1
$ErrorActionPreference = 'Stop'

# Script de ejemplo: muestra como importar la logica pura de lib/ y
# como usar los patrones del proyecto (timeout acotado, validacion).

$Here = Split-Path -Parent $MyInvocation.MyCommand.Path
Import-Module (Join-Path $Here 'lib\Core.psm1') -ErrorAction Stop

if (-not (Test-SafePort -Port 9010)) {
    throw 'El puerto 9010 no es valido.'
}

$token = New-TimeoutToken -Milliseconds 100
try {
    # Operacion "que deberia ser rapida": con el token, nunca puede colgarse.
    $token.Token.WaitHandle.WaitOne(1000) | Out-Null
    if ($token.IsCancellationRequested) {
        Write-Host 'Timeout disparado correctamente: la operacion esta acotada.'
    }
}
finally {
    $token.Dispose()
}
