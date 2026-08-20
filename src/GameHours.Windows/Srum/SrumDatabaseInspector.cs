using Microsoft.Isam.Esent.Interop;

namespace GameHours.Windows.Srum;

public sealed class SrumDatabaseInspector
{
    public IReadOnlyList<SrumTableSchema> Inspect(string databasePath)
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

        try
        {
            Api.JetGetDatabaseFileInfo(fullPath, out int pageSize, JET_DbInfo.PageSize);
            SystemParameters.DatabasePageSize = pageSize;

            using var instance = new Instance($"GameHoursSrumInspector-{Guid.NewGuid():N}");
            instance.Parameters.Recovery = false;
            instance.Init();

            using var session = new Session(instance);
            Api.JetAttachDatabase(session, fullPath, AttachDatabaseGrbit.ReadOnly);

            try
            {
                Api.OpenDatabase(session, fullPath, out var dbid, OpenDatabaseGrbit.ReadOnly);
                try
                {
                    var tables = new List<SrumTableSchema>();
                    foreach (var tableName in Api.GetTableNames(session, dbid)
                                 .OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
                    {
                        if (!Api.TryOpenTable(
                                session,
                                dbid,
                                tableName,
                                OpenTableGrbit.None,
                                out var tableid))
                        {
                            continue;
                        }

                        try
                        {
                            var columns = Api.GetColumnDictionary(session, tableid)
                                .Keys
                                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                                .ToArray();
                            tables.Add(new SrumTableSchema(tableName, columns));
                        }
                        finally
                        {
                            Api.JetCloseTable(session, tableid);
                        }
                    }

                    return tables;
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
                $"Could not inspect SRUM database '{fullPath}': {exception.Message}",
                exception);
        }
    }
}
