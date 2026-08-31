using GameHours.Core.Domain;

namespace GameHours.Core.Abstractions;

public interface IExecutableMappingRepository
{
    Task<ExecutableMapping?> FindByPathAsync(
        string executablePath,
        CancellationToken cancellationToken = default);

    Task UpsertAsync(
        ExecutableMapping mapping,
        CancellationToken cancellationToken = default);

    Task DeleteByPathAsync(
        string executablePath,
        CancellationToken cancellationToken = default);
}
