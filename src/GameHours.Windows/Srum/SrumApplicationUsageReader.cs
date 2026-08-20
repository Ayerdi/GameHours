using System.Security.Principal;
using System.Text;
using Microsoft.Isam.Esent.Interop;

namespace GameHours.Windows.Srum;

public sealed class SrumApplicationUsageReader
{
    public const string ApplicationResourceUsageTable = "{D10CA2FE-6FCF-4F6D-848E-B2E99266FA89}";

    public IReadOnlyList<SrumApplicationUsage> Read(
        string databasePath,
        DateTimeOffset? throughUtc = null,
        string? userSid = null)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            throw new ArgumentException("SRUM database path cannot be empty.", nameof(databasePath));
        }

        var fullPath = Path.GetFullPath(databasePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("SRUM database was not found.", fullPath);
        }

        var normalizedThrough = throughUtc?.ToUniversalTime();

        try
        {
            Api.JetGetDatabaseFileInfo(fullPath, out int pageSize, JET_DbInfo.PageSize);
            SystemParameters.DatabasePageSize = pageSize;

            using var instance = new Instance($"GameHoursSrumUsage-{Guid.NewGuid():N}");
            instance.Parameters.Recovery = false;
            instance.Init();

            using var session = new Session(instance);
            Api.JetAttachDatabase(session, fullPath, AttachDatabaseGrbit.ReadOnly);

            try
            {
                Api.OpenDatabase(session, fullPath, out var dbid, OpenDatabaseGrbit.ReadOnly);
                try
                {
                    var idMap = ReadIdMap(session, dbid);
                    return ReadUsageRows(
                        session,
                        dbid,
                        idMap,
                        normalizedThrough,
                        string.IsNullOrWhiteSpace(userSid) ? null : userSid.Trim());
                }
                finally
                {
                    Api.JetCloseDatabase(session, dbid, CloseDatabaseGrbit.None);
                }
            }
            finally
            {
                Api.JetDetachDatabase(session, fullPath);
            }
        }
        catch (EsentErrorException exception)
        {
            throw new InvalidOperationException(
                $"Could not read SRUM database '{fullPath}': {exception.Message}",
                exception);
        }
    }

    private static IReadOnlyDictionary<int, SrumIdMapEntry> ReadIdMap(
        JET_SESID session,
        JET_DBID dbid)
    {
        if (!Api.TryOpenTable(
                session,
                dbid,
                "SruDbIdMapTable",
                OpenTableGrbit.None,
                out var tableid))
        {
            throw new InvalidOperationException("SRUM SruDbIdMapTable was not found.");
        }

        try
        {
            var columns = Api.GetColumnDictionary(session, tableid);
            var idTypeColumn = RequireColumn(columns, "IdType", "SruDbIdMapTable");
            var idIndexColumn = RequireColumn(columns, "IdIndex", "SruDbIdMapTable");
            var idBlobColumn = RequireColumn(columns, "IdBlob", "SruDbIdMapTable");
            var results = new Dictionary<int, SrumIdMapEntry>();

            Api.MoveBeforeFirst(session, tableid);
            while (Api.TryMoveNext(session, tableid))
            {
                var idType = Api.RetrieveColumnAsByte(session, tableid, idTypeColumn);
                var idIndex = Api.RetrieveColumnAsInt32(session, tableid, idIndexColumn);
                var blob = Api.RetrieveColumn(session, tableid, idBlobColumn);

                if (idType is null || idIndex is null || blob is null || blob.Length == 0)
                {
                    continue;
                }

                results[idIndex.Value] = new SrumIdMapEntry(idType.Value, blob);
            }

            return results;
        }
        finally
        {
            Api.JetCloseTable(session, tableid);
        }
    }

    private static IReadOnlyList<SrumApplicationUsage> ReadUsageRows(
        JET_SESID session,
        JET_DBID dbid,
        IReadOnlyDictionary<int, SrumIdMapEntry> idMap,
        DateTimeOffset? throughUtc,
        string? userSid)
    {
        if (!Api.TryOpenTable(
                session,
                dbid,
                ApplicationResourceUsageTable,
                OpenTableGrbit.None,
                out var tableid))
        {
            throw new InvalidOperationException(
                $"SRUM application resource table {ApplicationResourceUsageTable} was not found.");
        }

        try
        {
            var columns = Api.GetColumnDictionary(session, tableid);
            var timestampColumn = RequireColumn(columns, "TimeStamp", ApplicationResourceUsageTable);
            var appIdColumn = RequireColumn(columns, "AppId", ApplicationResourceUsageTable);
            var userIdColumn = RequireColumn(columns, "UserId", ApplicationResourceUsageTable);
            var faceTimeColumn = RequireColumn(columns, "FaceTime", ApplicationResourceUsageTable);
            var results = new List<SrumApplicationUsage>();

            Api.MoveBeforeFirst(session, tableid);
            while (Api.TryMoveNext(session, tableid))
            {
                var appId = Api.RetrieveColumnAsInt32(session, tableid, appIdColumn);
                var rowUserId = Api.RetrieveColumnAsInt32(session, tableid, userIdColumn);
                var timestamp = Api.RetrieveColumnAsDateTime(session, tableid, timestampColumn);
                var faceTimeTicks = Api.RetrieveColumnAsInt64(session, tableid, faceTimeColumn);

                if (appId is null || timestamp is null || faceTimeTicks is null || faceTimeTicks <= 0)
                {
                    continue;
                }

                if (!idMap.TryGetValue(appId.Value, out var appMap))
                {
                    continue;
                }

                var application = DecodeText(appMap);
                if (string.IsNullOrWhiteSpace(application))
                {
                    continue;
                }

                var resolvedUserSid = rowUserId is not null &&
                    idMap.TryGetValue(rowUserId.Value, out var userMap)
                        ? DecodeSid(userMap)
                        : null;

                if (userSid is not null &&
                    !string.Equals(resolvedUserSid, userSid, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // SRUM stores this provider's timestamp as an OLE/ESENT DateTime representing UTC.
                var recordedAtUtc = new DateTimeOffset(
                    DateTime.SpecifyKind(timestamp.Value, DateTimeKind.Utc));

                // The Application Resource Usage FaceTime value is expressed in 100 ns units,
                // the same unit as a .NET TimeSpan tick.
                var faceTime = TimeSpan.FromTicks(faceTimeTicks.Value);

                // A row timestamp is the observation/sample boundary. Excluding rows recorded
                // after the immutable cutover is conservative and prevents historical baseline
                // evidence from claiming measured GameHours time.
                if (throughUtc is not null && recordedAtUtc > throughUtc.Value)
                {
                    continue;
                }

                results.Add(new SrumApplicationUsage(
                    appId.Value,
                    application,
                    resolvedUserSid,
                    recordedAtUtc,
                    faceTime));
            }

            return results;
        }
        finally
        {
            Api.JetCloseTable(session, tableid);
        }
    }

    private static JET_COLUMNID RequireColumn(
        IReadOnlyDictionary<string, JET_COLUMNID> columns,
        string name,
        string tableName)
    {
        if (!columns.TryGetValue(name, out var column))
        {
            throw new InvalidOperationException(
                $"Required SRUM column '{name}' is missing from table '{tableName}'.");
        }

        return column;
    }

    private static string? DecodeText(SrumIdMapEntry entry)
    {
        if (entry.IdType == 3)
        {
            return null;
        }

        return Encoding.Unicode
            .GetString(entry.Blob)
            .TrimEnd('\0')
            .Trim();
    }

    private static string? DecodeSid(SrumIdMapEntry entry)
    {
        if (entry.IdType != 3)
        {
            return null;
        }

        try
        {
            return new SecurityIdentifier(entry.Blob, 0).Value;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private sealed record SrumIdMapEntry(byte IdType, byte[] Blob);
}
