#requires -Version 5.1

# Logica pura y testeable: sin dependencias de hardware, red ni G HUB.
# Todo lo que pueda fallar aqui debe poder comprobarse con Pester sin entorno real.

function New-TimeoutToken {
    # Crea un CancellationTokenSource que se autocancela tras N milisegundos.
    # Patron para acotar operaciones async (WebSocket, HTTP, etc.) que en
    # .NET/PS 5.1 no tienen timeout nativo.
    param([Parameter(Mandatory=$true)][int]$Milliseconds)

    if ($Milliseconds -le 0) {
        throw "Milliseconds debe ser positivo (recibido $Milliseconds)."
    }

    $cts = New-Object System.Threading.CancellationTokenSource
    $cts.CancelAfter($Milliseconds)
    return $cts
}

function Test-SafePort {
    # Un puerto TCP valido es 1..65535 (0 es reservado/ligado aleatorio).
    param([int]$Port)

    return (($Port -ge 1) -and ($Port -le 65535))
}

Export-ModuleMember -Function New-TimeoutToken, Test-SafePort
