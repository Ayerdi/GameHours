using GameHours.Core.Abstractions;
using GameHours.Core.Monitoring;

namespace GameHours.Core.Discovery;

public sealed class CandidateRecordingGameResolver : IGameResolver
{
    private readonly IGameResolver _inner;
    private readonly IGameCandidateRepository _candidates;
    private readonly double _automaticTrackingThreshold;
    private readonly TimeProvider _timeProvider;

    public CandidateRecordingGameResolver(
        IGameResolver inner,
        IGameCandidateRepository candidates,
        double automaticTrackingThreshold = 0.80,
        TimeProvider? timeProvider = null)
    {
        if (automaticTrackingThreshold is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(automaticTrackingThreshold));
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _candidates = candidates ?? throw new ArgumentNullException(nameof(candidates));
        _automaticTrackingThreshold = automaticTrackingThreshold;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public event Action? CandidateRecorded;

    public async Task<GameResolution> ResolveAsync(ProcessSnapshot process, CancellationToken cancellationToken = default)
    {
        var path = NormalizePath(process.ExecutablePath);
        if (path is not null)
        {
            var decided = await _candidates.GetByPathAsync(path, cancellationToken);
            if (decided is { Status: not GameCandidateStatus.Pending, DecisionRole: { } role } && role.IsHelperLike())
            {
                return new GameResolution(
                    null,
                    0,
                    "user_candidate_decision",
                    true,
                    role,
                    new[]
                    {
                        new GameDetectionEvidence(
                            GameDetectionEvidenceKind.ExecutableRole,
                            -1,
                            $"Persisted user decision: {role}")
                    });
            }
        }

        var resolution = await _inner.ResolveAsync(process, cancellationToken);
        if (!GameCandidateAdmissionPolicy.ShouldRecord(process, resolution, _automaticTrackingThreshold)) return resolution;

        try
        {
            path ??= Path.GetFullPath(process.ExecutablePath!);
            await _candidates.ObserveAsync(
                new GameCandidateObservation(
                    path,
                    process.ProcessName,
                    resolution.Game?.Title ?? Path.GetFileNameWithoutExtension(path),
                    resolution.Confidence,
                    resolution.Method,
                    resolution.Role,
                    resolution.DetectionEvidence,
                    _timeProvider.GetUtcNow()),
                cancellationToken);
            CandidateRecorded?.Invoke();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException) { }

        return resolution;
    }

    private static string? NormalizePath(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath)) return null;
        try { return Path.GetFullPath(executablePath); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }
}
