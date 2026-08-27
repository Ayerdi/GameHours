using GameHours.Core.Abstractions;
using GameHours.Core.Domain;
using GameHours.Core.Timeline;
using Microsoft.Data.Sqlite;

namespace GameHours.Storage.Sqlite;

public sealed class SqliteHistoricalEvidenceRepository : IHistoricalEvidenceRepository
{
    private readonly GameHoursDatabase _database;
    private readonly ITrackingStateRepository _trackingState;
    private readonly ISessionRepository _sessions;

    public SqliteHistoricalEvidenceRepository(GameHoursDatabase database, ITrackingStateRepository trackingState, ISessionRepository sessions)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _trackingState = trackingState ?? throw new ArgumentNullException(nameof(trackingState));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
    }

    public async Task<bool> AddAsync(HistoricalEvidence evidence, CancellationToken cancellationToken = default)
    {
        var cutover = await _trackingState.GetTrackingStartedAtAsync(cancellationToken) ?? throw new TimelineConflictException("tracking_started_at must be set before historical evidence is persisted.");
        PlaytimeTimelineRules.ValidateAgainstCutover(evidence, cutover);

        var existing = await GetForGameAsync(evidence.GameId, cancellationToken);
        if (existing.Any(item => item.Id == evidence.Id))
        {
            return false;
        }

        if (evidence.Kind is EvidenceKind.GapRecovery)
        {
            if (await _sessions.HasOverlapAsync(evidence.GameId, evidence.PeriodStartUtc, evidence.PeriodEndUtc, cancellationToken))
            {
                throw new TimelineConflictException("Gap recovery overlaps a measured GameHours session.");
            }

            if (existing.Any(item => PlaytimeTimelineRules.Overlaps(
                    item.PeriodStartUtc,
                    item.PeriodEndUtc,
                    evidence.PeriodStartUtc,
                    evidence.PeriodEndUtc)))
            {
                throw new TimelineConflictException("Gap recovery overlaps existing historical evidence.");
            }
        }

        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO historical_evidence(id, game_id, source, evidence_kind, metric, confidence, period_start_utc, period_end_utc, duration_ms, created_at_utc)
            VALUES($id, $gameId, $source, $kind, $metric, $confidence, $periodStartUtc, $periodEndUtc, $durationMs, $createdAtUtc)
            ON CONFLICT(id) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$id", evidence.Id.ToString("D"));
        command.Parameters.AddWithValue("$gameId", evidence.GameId.ToString("D"));
        command.Parameters.AddWithValue("$source", (int)evidence.Source);
        command.Parameters.AddWithValue("$kind", (int)evidence.Kind);
        command.Parameters.AddWithValue("$metric", (int)evidence.Metric);
        command.Parameters.AddWithValue("$confidence", (int)evidence.Confidence);
        command.Parameters.AddWithValue("$periodStartUtc", SqliteTime.Serialize(evidence.PeriodStartUtc));
        command.Parameters.AddWithValue("$periodEndUtc", SqliteTime.Serialize(evidence.PeriodEndUtc));
        command.Parameters.AddWithValue("$durationMs", checked((long)Math.Round(evidence.Duration.TotalMilliseconds)));
        command.Parameters.AddWithValue("$createdAtUtc", SqliteTime.Serialize(DateTimeOffset.UtcNow));
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public Task<IReadOnlyList<HistoricalEvidence>> GetForGameAsync(Guid gameId, CancellationToken cancellationToken = default) => QueryAsync(gameId, cancellationToken);
    public Task<IReadOnlyList<HistoricalEvidence>> GetAllAsync(CancellationToken cancellationToken = default) => QueryAsync(null, cancellationToken);

    private async Task<IReadOnlyList<HistoricalEvidence>> QueryAsync(Guid? gameId, CancellationToken cancellationToken)
    {
        var results = new List<HistoricalEvidence>();
        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT id, game_id, source, evidence_kind, metric, confidence, period_start_utc, period_end_utc, duration_ms
            FROM historical_evidence{(gameId is null ? string.Empty : " WHERE game_id = $gameId")}
            ORDER BY period_start_utc;
            """;
        if (gameId is { } id) command.Parameters.AddWithValue("$gameId", id.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) results.Add(ReadEvidence(reader));
        return results;
    }

    private static HistoricalEvidence ReadEvidence(SqliteDataReader reader) => new(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), (HistoricalSource)reader.GetInt32(2), (EvidenceKind)reader.GetInt32(3), (PlaytimeMetric)reader.GetInt32(4), (Confidence)reader.GetInt32(5), SqliteTime.Deserialize(reader.GetString(6)), SqliteTime.Deserialize(reader.GetString(7)), TimeSpan.FromMilliseconds(reader.GetInt64(8)));
}
