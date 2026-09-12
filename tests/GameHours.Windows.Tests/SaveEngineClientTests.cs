using GameHours.SaveSafety;

namespace GameHours.Windows.Tests;

public sealed class SaveEngineClientTests
{
    [Fact]
    public async Task GetCapabilities_ValidatesAndDeserializesEnvelope()
    {
        using var script = TempPowerShellScript.Create(
            """
            $request = [Console]::In.ReadToEnd() | ConvertFrom-Json
            $response = @{
              protocolVersion = 1
              requestId = $request.requestId
              ok = $true
              result = @{
                engineVersion = '0.1.0'
                ludusaviVersion = '0.31.0'
                ludusaviRevision = 'abc123'
                protocolVersion = 1
                operations = @('getCapabilities', 'previewSaveData')
              }
            } | ConvertTo-Json -Depth 6 -Compress
            [Console]::Out.Write($response)
            """);
        var client = script.CreateClient();

        var result = await client.GetCapabilitiesAsync();

        Assert.Equal("0.31.0", result.LudusaviVersion);
        Assert.Equal("abc123", result.LudusaviRevision);
        Assert.Contains("previewSaveData", result.Operations);
    }

    [Fact]
    public async Task GetCapabilities_RejectsMismatchedProtocol()
    {
        using var script = TempPowerShellScript.Create(
            """
            $request = [Console]::In.ReadToEnd() | ConvertFrom-Json
            [Console]::Out.Write((@{
              protocolVersion = 2
              requestId = $request.requestId
              ok = $true
              result = @{ engineVersion = 'x' }
            } | ConvertTo-Json -Depth 4 -Compress))
            """);
        var client = script.CreateClient();

        var error = await Assert.ThrowsAsync<SaveEngineException>(() => client.GetCapabilitiesAsync());

        Assert.Equal("ProtocolMismatch", error.Code);
    }

    [Fact]
    public async Task Invoke_TimesOutAndTerminatesSlowHelper()
    {
        using var script = TempPowerShellScript.Create("Start-Sleep -Seconds 5");
        var client = script.CreateClient(timeout: TimeSpan.FromMilliseconds(150));

        var error = await Assert.ThrowsAsync<SaveEngineException>(() => client.GetCapabilitiesAsync());

        Assert.Equal("Timeout", error.Code);
    }

    [Fact]
    public async Task Invoke_PropagatesCallerCancellationInsteadOfReportingTimeout()
    {
        using var script = TempPowerShellScript.Create("Start-Sleep -Seconds 5");
        var client = script.CreateClient(timeout: TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetCapabilitiesAsync(cancellation.Token));
    }

    [Fact]
    public async Task Invoke_PreservesStructuredEngineErrorCode()
    {
        using var script = TempPowerShellScript.Create(
            """
            $request = [Console]::In.ReadToEnd() | ConvertFrom-Json
            [Console]::Out.Write((@{
              protocolVersion = 1
              requestId = $request.requestId
              ok = $false
              error = @{ code = 'UnsupportedGame'; message = 'fixture is unsupported' }
            } | ConvertTo-Json -Depth 4 -Compress))
            """);
        var client = script.CreateClient();

        var error = await Assert.ThrowsAsync<SaveEngineException>(() => client.GetCapabilitiesAsync());

        Assert.Equal("UnsupportedGame", error.Code);
        Assert.Equal("fixture is unsupported", error.Message);
    }

    [Fact]
    public async Task Invoke_RejectsOversizedStdout()
    {
        using var script = TempPowerShellScript.Create("[Console]::Out.Write(('x' * 2048))");
        var client = script.CreateClient(maxStdoutChars: 128);

        var error = await Assert.ThrowsAsync<SaveEngineException>(() => client.GetCapabilitiesAsync());

        Assert.Equal("OutputLimit", error.Code);
    }

    private sealed class TempPowerShellScript : IDisposable
    {
        private TempPowerShellScript(string path) => Path = path;

        public string Path { get; }

        public static TempPowerShellScript Create(string content)
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"gamehours-saveengine-{Guid.NewGuid():N}.ps1");
            File.WriteAllText(path, content);
            return new TempPowerShellScript(path);
        }

        public SaveEngineClient CreateClient(
            TimeSpan? timeout = null,
            int maxStdoutChars = SaveEngineClient.DefaultMaxStdoutChars) =>
            new(
                "pwsh",
                ["-NoProfile", "-NonInteractive", "-File", Path],
                timeout,
                maxStdoutChars);

        public void Dispose()
        {
            try { File.Delete(Path); } catch { }
        }
    }
}
