using GameHours.Core.Domain;

namespace GameHours.Windows.Achievements.Evidence;

public sealed record AchievementEvidenceAggregateResult(
    IReadOnlyList<ConfirmedAchievementUnlockEvidence> Evidence,
    IReadOnlyList<AchievementEvidenceDiagnostic> Diagnostics)
{
    public IReadOnlyList<string> ConfirmedApiNames => Evidence
        .Select(item => item.ApiName)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public IReadOnlyList<AchievementEvidenceRuleIdentity> ActiveRuleIdentities { get; init; } =
        Array.Empty<AchievementEvidenceRuleIdentity>();

    public bool HasFailures => Diagnostics.Count > 0;
}

/// <summary>
/// Runs every registered evidence provider because several independent positive proofs may
/// legitimately coexist. A failed provider does not erase evidence produced by another one.
/// Applicable providers also contribute their active rule identities so downstream projection
/// does not need game/provider-specific rule knowledge.
/// </summary>
public sealed class AchievementEvidenceProviderChain
{
    private readonly IReadOnlyList<IAchievementUnlockEvidenceProvider> _providers;

    public AchievementEvidenceProviderChain(IEnumerable<IAchievementUnlockEvidenceProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _providers = providers.ToArray();
    }

    public async Task<AchievementEvidenceAggregateResult> ReadAsync(
        AchievementEvidenceRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request = request.Normalize();

        var evidence = new List<ConfirmedAchievementUnlockEvidence>();
        var diagnostics = new List<AchievementEvidenceDiagnostic>();
        var activeRules = new HashSet<AchievementEvidenceRuleIdentity>();

        foreach (var provider in _providers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(provider);

            AchievementEvidenceReadResult result;
            try
            {
                result = await provider.ReadAsync(request, cancellationToken);
            }
            catch (Exception exception) when (IsEnvironmentalFailure(exception))
            {
                diagnostics.Add(new AchievementEvidenceDiagnostic(
                    provider.Name,
                    exception.Message));
                continue;
            }

            if (result.Status != AchievementEvidenceReadStatus.NotApplicable)
            {
                foreach (var identity in result.ActiveRuleIdentities)
                {
                    if (!string.Equals(identity.Provider, result.Provider, StringComparison.OrdinalIgnoreCase))
                    {
                        diagnostics.Add(new AchievementEvidenceDiagnostic(
                            provider.Name,
                            $"Provider reported active rule '{identity.RuleId}' under unrelated provider '{identity.Provider}'."));
                        continue;
                    }

                    activeRules.Add(identity);
                }
            }

            if (result.Status == AchievementEvidenceReadStatus.Failed)
            {
                diagnostics.AddRange(result.Diagnostics);
                continue;
            }

            diagnostics.AddRange(result.Diagnostics);

            if (!result.IsSuccess)
            {
                continue;
            }

            foreach (var proof in result.Evidence)
            {
                if (proof.GameId != request.GameId)
                {
                    diagnostics.Add(new AchievementEvidenceDiagnostic(
                        provider.Name,
                        $"Provider returned evidence for game {proof.GameId:D} while {request.GameId:D} was requested.",
                        proof.SourcePath));
                    continue;
                }

                evidence.Add(proof);
            }
        }

        return new AchievementEvidenceAggregateResult(
            Deduplicate(evidence),
            diagnostics.ToArray())
        {
            ActiveRuleIdentities = activeRules
                .OrderBy(identity => identity.Provider, StringComparer.OrdinalIgnoreCase)
                .ThenBy(identity => identity.AchievementApiName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(identity => identity.RuleId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(identity => identity.RuleVersion)
                .ToArray()
        };
    }

    private static IReadOnlyList<ConfirmedAchievementUnlockEvidence> Deduplicate(
        IEnumerable<ConfirmedAchievementUnlockEvidence> evidence)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<ConfirmedAchievementUnlockEvidence>();

        foreach (var item in evidence
                     .OrderBy(item => item.ApiName, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => item.Provider, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => item.RuleId, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => item.RuleVersion))
        {
            var key = string.Join(
                '\u001f',
                item.GameId.ToString("N"),
                item.ApiName,
                item.Provider,
                item.RuleId,
                item.RuleVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
                item.SourceFingerprint ?? string.Empty);

            if (seen.Add(key))
            {
                results.Add(item);
            }
        }

        return results;
    }

    private static bool IsEnvironmentalFailure(Exception exception) =>
        exception is IOException or
            UnauthorizedAccessException or
            InvalidDataException or
            FormatException or
            PathTooLongException;
}
