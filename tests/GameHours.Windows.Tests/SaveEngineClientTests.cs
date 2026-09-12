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
            if ($request.protocolVersion -ne 2) { throw 'unexpected protocol version' }
            $response = @{
              protocolVersion = 2
              requestId = $request.requestId
              ok = $true
              result = @{
                engineVersion = '0.1.0'
                ludusaviVersion = '0.31.0'
                ludusaviRevision = 'abc123'
                protocolVersion = 2
                operations = @('getCapabilities', 'previewSaveData')
                dataScopes = @('allAssociated', 'portableSave')
              }
            } | ConvertTo-Json -Depth 6 -Compress
            [Console]::Out.Write($response)
            """);
        var client = script.CreateClient();

        var result = await client.GetCapabilitiesAsync();

        Assert.Equal("0.31.0", result.LudusaviVersion);
        Assert.Equal("abc123", result.LudusaviRevision);
        Assert.Contains("previewSaveData", result.Operations);
        Assert.Contains("portableSave", result.DataScopes);
    }

    [Fact]
    public async Task GetCapabilities_RejectsMismatchedProtocol()
    {
        using var script = TempPowerShellScript.Create(
            """
            $request = [Console]::In.ReadToEnd() | ConvertFrom-Json
            [Console]::Out.Write((@{
              protocolVersion = 1
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
              protocolVersion = 2
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

    [Fact]
    public async Task PreviewGameSaveData_SendsStableIdentityAndDeserializesPreview()
    {
        using var script = TempPowerShellScript.Create(
            """
            $request = [Console]::In.ReadToEnd() | ConvertFrom-Json
            if ($request.operation -ne 'previewGameSaveData') { throw 'unexpected operation' }
            if ($request.payload.identity.store -ne 'steam') { throw 'unexpected store' }
            if ($request.payload.identity.externalId -ne '12345') { throw 'unexpected external id' }
            if ($request.payload.dataScope -ne 'portableSave') { throw 'unexpected data scope' }
            [Console]::Out.Write((@{
              protocolVersion = 2
              requestId = $request.requestId
              ok = $true
              result = @{
                gameName = 'Fixture Game'
                fileCount = 1
                totalBytes = 10
                registryKeyCount = 0
                files = @(@{ path = 'save.dat'; bytes = 10; ignored = $false; failed = $false })
                registryKeys = @()
                selection = @{
                  dataScope = 'portableSave'
                  saveFilterApplied = $true
                  retainedUnclassifiedEntries = $false
                  excludedConfigEntries = 1
                }
              }
            } | ConvertTo-Json -Depth 6 -Compress))
            """);
        var client = script.CreateClient();

        var result = await client.PreviewGameSaveDataAsync(
            "manifest.yaml",
            new SaveEngineGameIdentity("steam", "12345"),
            [new SaveEngineRoot("D:\\SteamLibrary", "steam")],
            dataScope: SaveDataScope.PortableSave);

        Assert.Equal("Fixture Game", result.GameName);
        Assert.Equal(1, result.FileCount);
        Assert.Equal(10, result.TotalBytes);
        Assert.Equal(SaveDataScope.PortableSave, result.Selection.DataScope);
        Assert.True(result.Selection.SaveFilterApplied);
        Assert.Equal(1, result.Selection.ExcludedConfigEntries);
    }

    [Fact]
    public async Task CreateGameBackup_SendsControlledDestinationAndDeserializesResult()
    {
        using var script = TempPowerShellScript.Create(
            """
            $request = [Console]::In.ReadToEnd() | ConvertFrom-Json
            if ($request.operation -ne 'createGameBackup') { throw 'unexpected operation' }
            if ($request.payload.identity.store -ne 'steam') { throw 'unexpected store' }
            if ($request.payload.identity.externalId -ne '12345') { throw 'unexpected external id' }
            if (-not [System.IO.Path]::IsPathFullyQualified([string]$request.payload.backupPath)) { throw 'backup path is not absolute' }
            if ($request.payload.dataScope -ne 'portableSave') { throw 'unexpected data scope' }
            [Console]::Out.Write((@{
              protocolVersion = 2
              requestId = $request.requestId
              ok = $true
              result = @{
                gameName = 'Fixture Game'
                fileCount = 3
                totalBytes = 42
                registryKeyCount = 1
                failedFileCount = 0
                failedRegistryKeyCount = 0
                changed = $true
                partial = $false
                selection = @{
                  dataScope = 'portableSave'
                  saveFilterApplied = $true
                  retainedUnclassifiedEntries = $false
                  excludedConfigEntries = 1
                }
              }
            } | ConvertTo-Json -Depth 6 -Compress))
            """);
        var client = script.CreateClient();
        var backupPath = Path.Combine(Path.GetTempPath(), "GameHours", "save-safety");

        var result = await client.CreateGameBackupAsync(
            "manifest.yaml",
            new SaveEngineGameIdentity("steam", "12345"),
            [new SaveEngineRoot("D:\\SteamLibrary", "steam")],
            backupPath,
            dataScope: SaveDataScope.PortableSave);

        Assert.Equal("Fixture Game", result.GameName);
        Assert.Equal(3, result.FileCount);
        Assert.Equal(42, result.TotalBytes);
        Assert.True(result.Changed);
        Assert.False(result.Partial);
        Assert.True(result.Selection.SaveFilterApplied);
    }

    [Fact]
    public async Task CreateGameBackup_RejectsRelativeDestinationBeforeStartingHelper()
    {
        using var script = TempPowerShellScript.Create("throw 'helper should not start'");
        var client = script.CreateClient();

        await Assert.ThrowsAsync<ArgumentException>(() => client.CreateGameBackupAsync(
            "manifest.yaml",
            new SaveEngineGameIdentity("steam", "12345"),
            [new SaveEngineRoot("D:\\SteamLibrary", "steam")],
            "relative\\backup"));
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
