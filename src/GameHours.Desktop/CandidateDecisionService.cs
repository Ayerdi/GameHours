using GameHours.Core.Abstractions;
using GameHours.Core.Discovery;
using GameHours.Core.Domain;
using GameHours.Storage.Sqlite;
using GameHours.Windows.Discovery;

namespace GameHours.Desktop;

public sealed class CandidateDecisionService
{
    private readonly IGameCandidateRepository _candidates;
    private readonly SqliteExecutableMappingRepository _mappings;
    private readonly IExecutableRoleOverrideStore _roleOverrides;

    public CandidateDecisionService(
        IGameCandidateRepository candidates,
        SqliteExecutableMappingRepository mappings,
        IExecutableRoleOverrideStore roleOverrides)
    {
        _candidates = candidates ?? throw new ArgumentNullException(nameof(candidates));
        _mappings = mappings ?? throw new ArgumentNullException(nameof(mappings));
        _roleOverrides = roleOverrides ?? throw new ArgumentNullException(nameof(roleOverrides));
    }

    public async Task ConfirmGameAsync(
        string executablePath,
        Guid gameId,
        ExecutableRole role,
        CancellationToken cancellationToken = default)
    {
        if (gameId == Guid.Empty) throw new ArgumentException("Game id cannot be empty.", nameof(gameId));
        if (role is not (ExecutableRole.PrimaryGame or ExecutableRole.SecondaryGame))
            throw new ArgumentException("A confirmed game must use a trackable game role.", nameof(role));

        var path = Path.GetFullPath(executablePath);
        await _mappings.UpsertAsync(new ExecutableMapping(gameId, path, false), cancellationToken);
        _roleOverrides.Remove(path);
        await _candidates.ResolveAsync(path, role, gameId, cancellationToken);
    }

    public async Task ClassifyHelperAsync(
        string executablePath,
        ExecutableRole role,
        Guid? gameId = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsClassifiableHelper(role))
            throw new ArgumentException("Role must be a launcher, helper, anti-cheat, updater or crash handler.", nameof(role));
        if (gameId == Guid.Empty) throw new ArgumentException("Game id cannot be empty.", nameof(gameId));

        var path = Path.GetFullPath(executablePath);
        if (gameId is { } associatedGameId)
        {
            await _mappings.UpsertAsync(new ExecutableMapping(associatedGameId, path, true), cancellationToken);
        }
        else
        {
            await _mappings.DeleteByPathAsync(path, cancellationToken);
        }

        _roleOverrides.SetRole(path, role);
        await _candidates.ResolveAsync(path, role, gameId, cancellationToken);
    }

    public async Task IgnoreAsync(string executablePath, CancellationToken cancellationToken = default)
    {
        var path = Path.GetFullPath(executablePath);
        await _mappings.DeleteByPathAsync(path, cancellationToken);
        _roleOverrides.SetRole(path, ExecutableRole.Ignored);
        await _candidates.ResolveAsync(path, ExecutableRole.Ignored, cancellationToken: cancellationToken);
    }

    private static bool IsClassifiableHelper(ExecutableRole role) => role is
        ExecutableRole.Launcher or
        ExecutableRole.Helper or
        ExecutableRole.AntiCheat or
        ExecutableRole.Updater or
        ExecutableRole.CrashHandler;
}
