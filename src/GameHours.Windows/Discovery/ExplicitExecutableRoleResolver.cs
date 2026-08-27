using GameHours.Core.Abstractions;
using GameHours.Core.Discovery;
using GameHours.Core.Monitoring;

namespace GameHours.Windows.Discovery;

public sealed class ExplicitExecutableRoleResolver : IGameResolver
{
    private readonly IGameResolver _inner;
    private readonly IExecutableRoleOverrideStore _roleOverrides;

    public ExplicitExecutableRoleResolver(
        IGameResolver inner,
        IExecutableRoleOverrideStore roleOverrides)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _roleOverrides = roleOverrides ?? throw new ArgumentNullException(nameof(roleOverrides));
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
