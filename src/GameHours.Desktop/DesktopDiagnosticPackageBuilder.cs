using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GameHours.Desktop;

internal sealed record DesktopDiagnosticPackageRequest(
    string DestinationPath,
    string Version,
    string Channel,
    DesktopRuntimeDiagnostics Runtime,
    string? ProblemDescription = null);

internal static partial class DesktopDiagnosticPackageBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static async Task CreateAsync(
        DesktopDiagnosticPackageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DestinationPath);

        var destination = Path.GetFullPath(request.DestinationPath);
        var directory = Path.GetDirectoryName(destination);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("Diagnostic package destination must have a parent directory.");
        }

        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.ReadWrite,
                             FileShare.None,
                             64 * 1024,
                             useAsync: true))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false))
            {
                var redactor = DiagnosticRedactor.CreateDefault();
                await WriteJsonAsync(archive, "summary.json", redactor, new
                {
                    generatedAtUtc = DateTimeOffset.UtcNow,
                    app = new
                    {
                        version = request.Version,
                        channel = request.Channel
                    },
                    runtime = new
                    {
                        os = RuntimeInformation.OSDescription,
                        osArchitecture = RuntimeInformation.OSArchitecture.ToString(),
                        processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                        framework = RuntimeInformation.FrameworkDescription,
                        processorCount = Environment.ProcessorCount
                    },
                    database = new
                    {
                        schemaVersion = request.Runtime.DatabaseSchemaVersion,
                        available = !string.IsNullOrWhiteSpace(request.Runtime.DatabasePath)
                    }
                }, cancellationToken);

                await WriteJsonAsync(archive, "runtime.json", redactor, new
                {
                    tracking = new
                    {
                        isTracking = request.Runtime.IsTracking,
                        hasActiveGame = !string.IsNullOrWhiteSpace(request.Runtime.ActiveGameTitle),
                        appliedAfkTimeoutMinutes = request.Runtime.AppliedAfkTimeoutMinutes,
                        processMonitor = request.Runtime.ProcessMonitor
                    },
                    preferences = new
                    {
                        afkTimeoutMinutes = request.Runtime.Preferences.AfkTimeoutMinutes,
                        afkFilterEnabled = request.Runtime.Preferences.AfkFilterEnabled,
                        lowImpactMode = request.Runtime.Preferences.LowImpactMode
                    },
                    process = new
                    {
                        cpuTimeSeconds = request.Runtime.ProcessCpuTime.TotalSeconds,
                        privateMemoryBytes = request.Runtime.PrivateMemoryBytes,
                        workingSetBytes = request.Runtime.WorkingSetBytes,
                        threadCount = request.Runtime.ThreadCount,
                        managedHeapBytes = request.Runtime.ManagedHeapBytes,
                        totalAllocatedBytes = request.Runtime.TotalAllocatedBytes,
                        gen0Collections = request.Runtime.Gen0CollectionCount,
                        gen1Collections = request.Runtime.Gen1CollectionCount,
                        gen2Collections = request.Runtime.Gen2CollectionCount,
                        gcCommittedBytes = request.Runtime.GcCommittedBytes,
                        gcFragmentedBytes = request.Runtime.GcFragmentedBytes,
                        gcPauseSeconds = request.Runtime.GcTotalPauseDuration.TotalSeconds
                    }
                }, cancellationToken);

                var readme = "GameHours diagnostic package\n" +
                             "Generated locally on explicit user request.\n" +
                             "This package does not include the GameHours database, SRUM, registry exports, tokens, raw local files, or full machine paths.\n";
                await WriteTextAsync(archive, "README.txt", redactor.Redact(readme), cancellationToken);

                if (!string.IsNullOrWhiteSpace(request.ProblemDescription))
                {
                    await WriteTextAsync(
                        archive,
                        "problem.txt",
                        redactor.Redact(request.ProblemDescription.Trim()),
                        cancellationToken);
                }
            }

            File.Move(temporaryPath, destination, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            catch
            {
            }
        }
    }

    private static async Task WriteJsonAsync(
        ZipArchive archive,
        string name,
        DiagnosticRedactor redactor,
        object value,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(value, JsonOptions);
        await WriteTextAsync(archive, name, redactor.Redact(json), cancellationToken);
    }

    private static async Task WriteTextAsync(
        ZipArchive archive,
        string name,
        string value,
        CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        cancellationToken.ThrowIfCancellationRequested();
        await writer.WriteAsync(value.AsMemory(), cancellationToken);
    }

    internal sealed partial class DiagnosticRedactor
    {
        private readonly IReadOnlyList<(string Value, string Replacement)> _values;

        private DiagnosticRedactor(IReadOnlyList<(string Value, string Replacement)> values) =>
            _values = values;

        public static DiagnosticRedactor CreateDefault()
        {
            var values = new List<(string Value, string Replacement)>
            {
                (Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "<USERPROFILE>"),
                (Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "<LOCALAPPDATA>"),
                (Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "<APPDATA>"),
                (Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), "<TEMP>"),
                (Environment.UserName, "<USER>")
            };

            return new DiagnosticRedactor(values
                .Where(item => !string.IsNullOrWhiteSpace(item.Value))
                .DistinctBy(item => item.Value, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(item => item.Value.Length)
                .ToArray());
        }

        public string Redact(string value)
        {
            var redacted = value ?? string.Empty;
            foreach (var (candidate, replacement) in _values)
            {
                redacted = redacted.Replace(candidate, replacement, StringComparison.OrdinalIgnoreCase);
            }

            redacted = WindowsUserPathRegex().Replace(redacted, @"$1<USER>$3");
            redacted = SecretAssignmentRegex().Replace(redacted, "$1=<REDACTED>");
            return BearerTokenRegex().Replace(redacted, "$1<REDACTED>");
        }

        [GeneratedRegex(@"(?i)([A-Z]:\\Users\\)([^\\\r\n]+)(\\)")]
        private static partial Regex WindowsUserPathRegex();

        [GeneratedRegex("""(?i)\b(token|password|secret|api[_-]?key)\s*[:=]\s*([^\s,;"']+)""")]
        private static partial Regex SecretAssignmentRegex();

        [GeneratedRegex(@"(?i)(\bBearer\s+)([A-Za-z0-9._~+/=-]+)")]
        private static partial Regex BearerTokenRegex();
    }
}
