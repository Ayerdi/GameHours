BeforeAll {
    $Module = Join-Path $PSScriptRoot '..\lib\Core.psm1'
    Import-Module $Module -Force -ErrorAction Stop
}

Describe 'New-TimeoutToken' {
    It 'throws on non-positive milliseconds' {
        { New-TimeoutToken -Milliseconds 0 } | Should -Throw
        { New-TimeoutToken -Milliseconds -1 } | Should -Throw
    }

    It 'cancels itself after the timeout' {
        $token = New-TimeoutToken -Milliseconds 50
        try {
            [System.Threading.Thread]::Sleep(150)
            $token.IsCancellationRequested | Should -BeTrue
        }
        finally {
            $token.Dispose()
        }
    }

    It 'does not cancel before the timeout' {
        $token = New-TimeoutToken -Milliseconds 5000
        try {
            $token.IsCancellationRequested | Should -BeFalse
        }
        finally {
            $token.Dispose()
        }
    }
}

Describe 'Test-SafePort' {
    It 'accepts valid ports' {
        Test-SafePort -Port 1     | Should -BeTrue
        Test-SafePort -Port 9010  | Should -BeTrue
        Test-SafePort -Port 65535 | Should -BeTrue
    }

    It 'rejects invalid ports' {
        Test-SafePort -Port 0     | Should -BeFalse
        Test-SafePort -Port -1    | Should -BeFalse
        Test-SafePort -Port 65536 | Should -BeFalse
    }
}
