using System.Text;
using HbysFhirBridge.Source;
using HbysFhirBridge.Sync;
using Oracle.ManagedDataAccess.Client;

namespace HbysFhirBridge.Oracle;

/// <summary>Parking table and checkpoint writes. Always called inside the batch transaction.</summary>
internal static class ParkingStore
{
    private const int MaxDetail = 3900;

    public static async Task<IReadOnlySet<SourceKey>> FindParkedAsync(
        OracleConnection connection, IReadOnlyCollection<SourceKey> keys, CancellationToken ct)
    {
        var result = new HashSet<SourceKey>();
        foreach (var group in keys.GroupBy(k => k.Table))
        {
            foreach (var chunk in group.Select(k => k.Id).Distinct().Chunk(OracleSql.InListChunk))
            {
                var sql = new StringBuilder("SELECT source_id FROM bridge_parked WHERE resource_type = :rt AND source_id IN (");
                await using var command = OracleSql.Command(connection, "");
                command.Parameters.Add("rt", OracleDbType.Varchar2).Value = SourceTables.ToResourceType(group.Key);
                OracleSql.AppendInList(sql, command, chunk);
                command.CommandText = sql.Append(')').ToString();
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    result.Add(new SourceKey(group.Key, reader.GetInt64(0)));
                }
            }
        }

        return result;
    }

    public static async Task ApplyAsync(
        OracleConnection connection, IReadOnlyCollection<SourceKey> delivered, IReadOnlyCollection<ParkedRow> parked, CancellationToken ct)
    {
        if (delivered.Count > 0)
        {
            await using var unpark = OracleSql.Command(connection,
                "DELETE FROM bridge_parked WHERE resource_type = :rt AND source_id = :id");
            unpark.ArrayBindCount = delivered.Count;
            unpark.Parameters.Add("rt", OracleDbType.Varchar2).Value = delivered.Select(k => SourceTables.ToResourceType(k.Table)).ToArray();
            unpark.Parameters.Add("id", OracleDbType.Int64).Value = delivered.Select(k => k.Id).ToArray();
            await unpark.ExecuteNonQueryAsync(ct);
        }

        if (parked.Count == 0)
        {
            return;
        }

        await using var park = OracleSql.Command(connection,
            "MERGE INTO bridge_parked t USING (SELECT :rt rt, :id sid FROM dual) s " +
            "ON (t.resource_type = s.rt AND t.source_id = s.sid) " +
            "WHEN MATCHED THEN UPDATE SET reason = :reason, detail = :detail, blocked_type = :bt, blocked_id = :bid, " +
            "last_parked = SYSTIMESTAMP, attempts = t.attempts + 1 " +
            "WHEN NOT MATCHED THEN INSERT (resource_type, source_id, reason, detail, blocked_type, blocked_id) " +
            "VALUES (s.rt, s.sid, :reason, :detail, :bt, :bid)");
        park.ArrayBindCount = parked.Count;
        park.Parameters.Add("rt", OracleDbType.Varchar2).Value = parked.Select(p => SourceTables.ToResourceType(p.Key.Table)).ToArray();
        park.Parameters.Add("id", OracleDbType.Int64).Value = parked.Select(p => p.Key.Id).ToArray();
        park.Parameters.Add("reason", OracleDbType.Varchar2).Value = parked.Select(p => p.Reason).ToArray();
        park.Parameters.Add("detail", OracleDbType.Varchar2).Value =
            parked.Select(p => p.Detail.Length <= MaxDetail ? p.Detail : p.Detail[..MaxDetail]).ToArray();
        park.Parameters.Add("bt", OracleDbType.Varchar2).Value =
            parked.Select(p => p.BlockedBy is { } b ? SourceTables.ToResourceType(b.Table) : null).ToArray();
        park.Parameters.Add("bid", OracleDbType.Int64).Value =
            parked.Select(p => p.BlockedBy is { } b ? (object)b.Id : DBNull.Value).ToArray();
        await park.ExecuteNonQueryAsync(ct);
    }

    public static async Task MoveCheckpointAsync(OracleConnection connection, string pipeline, string position, CancellationToken ct)
    {
        await using var command = OracleSql.Command(connection,
            "UPDATE bridge_checkpoint SET position = :pos, batches = batches + 1, updated_at = SYSTIMESTAMP WHERE pipeline = :p");
        command.Parameters.Add("pos", OracleDbType.Varchar2).Value = position;
        command.Parameters.Add("p", OracleDbType.Varchar2).Value = pipeline;
        await command.ExecuteNonQueryAsync(ct);
    }
}
