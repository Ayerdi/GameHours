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
        var resolution = await _inner.ResolveAsync(process, cancellationToken);
        if (!ShouldRecord(process, resolution)) return resolution;

        try
        {
            var path = Path.GetFullPath(process.ExecutablePath!);
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

    private bool ShouldRecord(ProcessSnapshot process, GameResolution resolution) =>
        !string.IsNullOrWhiteSpace(process.ExecutablePath) &&
        !resolution.IsHelperProcess &&
        resolution.Confidence < _automaticTrackingThreshold &&
        resolution.DetectionEvidence.Any(item => item.Weight > 0);
}
