using GameHours.Core.Abstractions;
using GameHours.Core.Discovery;
using GameHours.Core.Monitoring;

namespace GameHours.Windows.Discovery;

public sealed class ExplicitExecutableRoleResolver : IGameResolver
{
    private readonly IGameResolver _inner;
    private readonly IExecutableRoleOverrideStore _roleOverrides;
    private readonly IRecentProcessIdentityHistory _history;
    private readonly Func<DateTimeOffset> _utcNow;

    public ExplicitExecutableRoleResolver(
        IGameResolver inner,
        IExecutableRoleOverrideStore roleOverrides,
        IRecentProcessIdentityHistory? relationshipHistory = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _roleOverrides = roleOverrides ?? throw new ArgumentNullException(nameof(roleOverrides));
        _history = relationshipHistory ?? WindowsProcessRelationshipHistory.Shared;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public Task<GameResolution> ResolveAsync(
        ProcessSnapshot process,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = PathTools.Normalize(process.ExecutablePath);
        if (path is not null &&
            _roleOverrides.TryGetRole(path, out var role) &&
            role.IsHelperLike())
        {
            _history.Observe(
                process with { ExecutablePath = path },
                _utcNow().ToUniversalTime());

            return Task.FromResult(new GameResolution(
                null,
                0,
                "user_role_override",
                true,
                role,
                new[]
                {
                    new GameDetectionEvidence(
                        GameDetectionEvidenceKind.ExecutableRole,
                        -1,
                        $"User role override: {role}")
                }));
        }

        return _inner.ResolveAsync(process, cancellationToken);
    }
}
