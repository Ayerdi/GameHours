using GameHours.Core.Abstractions;
using GameHours.Core.Domain;

namespace GameHours.Windows.Achievements.Evidence;

/// <summary>
/// Result of one supplemental-evidence observation. AuditEvidence contains every durable proof,
/// including superseded revisions; ActiveEvidence contains only proofs accepted by the current
/// applicable rule set and is therefore the safe input for user-facing projections.
/// </summary>
public sealed record AchievementEvidenceObservation(
    AchievementEvidenceAggregateResult Scan,
    IReadOnlyList<StoredAchievementUnlockEvidence> AuditEvidence,
    IReadOnlyList<StoredAchievementUnlockEvidence> ActiveEvidence)
{
    public IReadOnlyList<string> ConfirmedApiNames => ActiveEvidence
        .Select(item => item.ApiName)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
        .ToArray();
}

/// <summary>
/// Generic orchestration boundary for supplemental achievement evidence. It persists newly
/// observed positive proofs, retains every historical revision for audit and projects only rule
/// revisions that applicable providers declare active in the current application.
///
/// Supplemental evidence deliberately stays separate from the monotonic platform/emulator
/// achievement repository so withdrawing an incorrect rule can also withdraw its projection.
/// </summary>
public sealed class AchievementEvidenceObservationService
{
    private readonly AchievementEvidenceProviderChain _providers;
    private readonly IAchievementEvidenceRepository _repository;

    public AchievementEvidenceObservationService(
        AchievementEvidenceProviderChain providers,
        IAchievementEvidenceRepository repository)
    {
        _providers = providers ?? throw new ArgumentNullException(nameof(providers));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    public async Task<AchievementEvidenceObservation> ObserveAsync(
        AchievementEvidenceRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request = request.Normalize();

        var scan = await _providers.ReadAsync(request, cancellationToken).ConfigureAwait(false);
        if (scan.Evidence.Count > 0)
        {
            await _repository.SaveAsync(request.GameId, scan.Evidence, cancellationToken)
                .ConfigureAwait(false);
        }

        var stored = await _repository.GetForGameAsync(request.GameId, cancellationToken)
            .ConfigureAwait(false);
        var auditEvidence = stored
            .Where(item => item.GameId == request.GameId)
            .ToArray();
        var activeEvidence = AchievementEvidenceRulePolicy.KeepActive(
            auditEvidence,
            scan.ActiveRuleIdentities);

        return new AchievementEvidenceObservation(
            scan,
            auditEvidence,
            activeEvidence);
    }
}
