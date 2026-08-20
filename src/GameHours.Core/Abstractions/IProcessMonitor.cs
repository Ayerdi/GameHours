using GameHours.Core.Monitoring;

namespace GameHours.Core.Abstractions;

public interface IProcessMonitor
{
    IAsyncEnumerable<ProcessObservation> ObserveAsync(CancellationToken cancellationToken = default);
}
