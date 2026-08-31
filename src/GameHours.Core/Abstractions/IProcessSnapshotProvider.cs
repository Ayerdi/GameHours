using GameHours.Core.Monitoring;

namespace GameHours.Core.Abstractions;

public interface IProcessSnapshotProvider
{
    Task<IReadOnlyList<ProcessSnapshot>> GetSnapshotAsync(CancellationToken cancellationToken = default);
}
