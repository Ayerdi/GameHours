namespace GameHours.Core.Abstractions;

public interface ITrackingStateRepository
{
    Task<DateTimeOffset?> GetTrackingStartedAtAsync(CancellationToken cancellationToken = default);

    Task<DateTimeOffset> GetOrSetTrackingStartedAtAsync(
        DateTimeOffset proposedUtc,
        CancellationToken cancellationToken = default);
}
