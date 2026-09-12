using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace GameHours.SaveSafety;

public sealed record SaveEngineRoot(string Path, string Store);

public sealed record SaveEngineGameIdentity(string Store, string ExternalId);

public sealed record SaveEngineCapabilities(
    string EngineVersion,
    string LudusaviVersion,
    string LudusaviRevision,
    int ProtocolVersion,
    string[] Operations);

public sealed record SaveDataFile(string Path, long Bytes, bool Ignored, bool Failed);

public sealed record SaveDataPreview(
    string GameName,
    int FileCount,
    long TotalBytes,
    int RegistryKeyCount,
    SaveDataFile[] Files,
    string[] RegistryKeys);

public sealed record SaveBackupResult(
    string GameName,
    int FileCount,
    long TotalBytes,
    int RegistryKeyCount,
    int FailedFileCount,
    int FailedRegistryKeyCount,
    bool Changed,
    bool Partial);

public sealed class SaveEngineException : Exception
{
    public SaveEngineException(string code, string message, Exception? innerException = null)
        : base(message, innerException) => Code = code;

    public string Code { get; }
}

public sealed class SaveEngineClient
{
    public const int ProtocolVersion = 1;
    public const int DefaultMaxStdoutChars = 1024 * 1024;
    public const int DefaultMaxStderrChars = 64 * 1024;
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _executablePath;
    private readonly string[] _arguments;
    private readonly TimeSpan _timeout;
    private readonly int _maxStdoutChars;
    private readonly int _maxStderrChars;

    public SaveEngineClient(
        string executablePath,
        IEnumerable<string>? arguments = null,
        TimeSpan? timeout = null,
        int maxStdoutChars = DefaultMaxStdoutChars,
        int maxStderrChars = DefaultMaxStderrChars)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        if (timeout is { } configuredTimeout && configuredTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        if (maxStdoutChars <= 0) throw new ArgumentOutOfRangeException(nameof(maxStdoutChars));
        if (maxStderrChars <= 0) throw new ArgumentOutOfRangeException(nameof(maxStderrChars));

        _executablePath = executablePath;
        _arguments = arguments?.ToArray() ?? [];
        _timeout = timeout ?? DefaultTimeout;
        _maxStdoutChars = maxStdoutChars;
        _maxStderrChars = maxStderrChars;
    }

    public Task<SaveEngineCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync<SaveEngineCapabilities>("getCapabilities", new { }, cancellationToken);

    public Task<SaveDataPreview> PreviewSaveDataAsync(
        string manifestPath,
        string gameName,
        IReadOnlyCollection<SaveEngineRoot> roots,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameName);
        ArgumentNullException.ThrowIfNull(roots);
        if (roots.Count == 0) throw new ArgumentException("At least one save root is required.", nameof(roots));

        return InvokeAsync<SaveDataPreview>(
            "previewSaveData",
            new { manifestPath, gameName, roots },
            cancellationToken);
    }

    public Task<SaveDataPreview> PreviewGameSaveDataAsync(
        string manifestPath,
        SaveEngineGameIdentity identity,
        IReadOnlyCollection<SaveEngineRoot> roots,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.Store);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.ExternalId);
        ArgumentNullException.ThrowIfNull(roots);
        if (roots.Count == 0) throw new ArgumentException("At least one save root is required.", nameof(roots));

        return InvokeAsync<SaveDataPreview>(
            "previewGameSaveData",
            new { manifestPath, identity, roots },
            cancellationToken);
    }

    public Task<SaveBackupResult> CreateGameBackupAsync(
        string manifestPath,
        SaveEngineGameIdentity identity,
        IReadOnlyCollection<SaveEngineRoot> roots,
        string backupPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.Store);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.ExternalId);
        ArgumentNullException.ThrowIfNull(roots);
        if (roots.Count == 0) throw new ArgumentException("At least one save root is required.", nameof(roots));
        ArgumentException.ThrowIfNullOrWhiteSpace(backupPath);
        if (!Path.IsPathFullyQualified(backupPath))
            throw new ArgumentException("Backup path must be fully qualified.", nameof(backupPath));

        return InvokeAsync<SaveBackupResult>(
            "createGameBackup",
            new { manifestPath, identity, roots, backupPath },
            cancellationToken);
    }

    private async Task<T> InvokeAsync<T>(string operation, object payload, CancellationToken cancellationToken)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var request = JsonSerializer.Serialize(
            new RequestEnvelope(ProtocolVersion, requestId, operation, payload),
            JsonOptions);

        var startInfo = new ProcessStartInfo
        {
            FileName = _executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in _arguments) startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start()) throw new SaveEngineException("EngineFailure", "SaveEngine did not start.");
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            throw new SaveEngineException("EngineFailure", "SaveEngine could not be started.", exception);
        }

        using var timeout = new CancellationTokenSource(_timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var token = linked.Token;

        var stdoutTask = ReadBoundedAsync(
            process.StandardOutput,
            _maxStdoutChars,
            () => TryKill(process),
            token);
        var stderrTask = ReadBoundedAsync(
            process.StandardError,
            _maxStderrChars,
            () => TryKill(process),
            token);

        try
        {
            try
            {
                await process.StandardInput.WriteAsync(request.AsMemory(), token);
                await process.StandardInput.FlushAsync(token);
                process.StandardInput.Close();
            }
            catch (IOException exception)
            {
                // A helper can close stdin while one of the bounded output readers is already
                // terminating it (for example after exceeding the stdout/stderr limit). Surface
                // that structured reader failure instead of racing it with a broken-pipe error.
                try
                {
                    await Task.WhenAll(stdoutTask, stderrTask);
                }
                catch (SaveEngineException)
                {
                    throw;
                }

                throw new SaveEngineException(
                    "EngineFailure",
                    "SaveEngine closed its input before the request was written.",
                    exception);
            }

            await process.WaitForExitAsync(token);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            ResponseEnvelope? response;
            try
            {
                response = JsonSerializer.Deserialize<ResponseEnvelope>(stdout, JsonOptions);
            }
            catch (JsonException exception)
            {
                throw new SaveEngineException(
                    "EngineFailure",
                    FormatInvalidResponseMessage(process.ExitCode, stderr),
                    exception);
            }

            if (response is null)
                throw new SaveEngineException("EngineFailure", FormatInvalidResponseMessage(process.ExitCode, stderr));
            if (response.ProtocolVersion != ProtocolVersion)
                throw new SaveEngineException("ProtocolMismatch", $"SaveEngine protocol {response.ProtocolVersion} is not supported.");
            if (!string.Equals(response.RequestId, requestId, StringComparison.Ordinal))
                throw new SaveEngineException("ProtocolMismatch", "SaveEngine response requestId did not match the request.");
            if (!response.Ok)
                throw new SaveEngineException(
                    response.Error?.Code ?? "EngineFailure",
                    response.Error?.Message ?? "SaveEngine reported an unspecified failure.");
            if (process.ExitCode != 0)
                throw new SaveEngineException("EngineFailure", $"SaveEngine exited with code {process.ExitCode}.");
            if (response.Result.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
                throw new SaveEngineException("ProtocolMismatch", "SaveEngine returned no result for a successful response.");

            return response.Result.Deserialize<T>(JsonOptions)
                   ?? throw new SaveEngineException("ProtocolMismatch", "SaveEngine returned an invalid result payload.");
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new SaveEngineException("Timeout", $"SaveEngine exceeded the {_timeout.TotalSeconds:0.###} second timeout.");
        }
        catch
        {
            TryKill(process);
            throw;
        }
    }

    private static async Task<string> ReadBoundedAsync(
        StreamReader reader,
        int maxChars,
        Action onLimitExceeded,
        CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        var builder = new System.Text.StringBuilder(Math.Min(maxChars, 16 * 1024));
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0) return builder.ToString();
            if (builder.Length + read > maxChars)
            {
                onLimitExceeded();
                throw new SaveEngineException("OutputLimit", $"SaveEngine output exceeded the {maxChars} character limit.");
            }

            builder.Append(buffer, 0, read);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best effort: callers still receive the original timeout/protocol/output error.
        }
    }

    private static string FormatInvalidResponseMessage(int exitCode, string stderr) =>
        string.IsNullOrWhiteSpace(stderr)
            ? $"SaveEngine returned invalid JSON and exited with code {exitCode}."
            : $"SaveEngine returned invalid JSON and exited with code {exitCode}: {stderr.Trim()}";

    private sealed record RequestEnvelope(int ProtocolVersion, string RequestId, string Operation, object Payload);
    private sealed record ResponseEnvelope(
        int ProtocolVersion,
        string RequestId,
        bool Ok,
        JsonElement Result,
        SaveEngineError? Error);
    private sealed record SaveEngineError(string Code, string Message);
}
