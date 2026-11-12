using HbysFhirBridge.Source;
using Oracle.ManagedDataAccess.Client;

namespace HbysFhirBridge.Oracle;

public sealed record BridgeStatus(
    long PendingChanges,
    DateTime? OldestPendingAt,
    string? Position,
    long Batches,
    IReadOnlyDictionary<string, long> ParkedByReason)
{
    public long Parked => ParkedByReason.Values.Sum();
}

/// <summary>Operator view: journal backlog, checkpoint and parking counts (backs /status and the health check).</summary>
public sealed class BridgeStatusReader(OracleSourceOptions options)
{
    private readonly string _schema = OracleSql.Identifier(options.SourceSchema);

    public async Task<BridgeStatus> ReadAsync(CancellationToken ct = default)
    {
        await using var connection = new OracleConnection(options.ConnectionString);
        await connection.OpenAsync(ct);

        long pending;
        DateTime? oldest;
        await using (var c = OracleSql.Command(connection, $"SELECT COUNT(*), MIN(islem_zamani) FROM {_schema}.degisiklik_gunlugu"))
        await using (var r = await c.ExecuteReaderAsync(ct))
        {
            await r.ReadAsync(ct);
            pending = r.GetInt64(0);
            oldest = r.IsDBNull(1) ? null : r.GetDateTime(1);
        }

        string? position = null;
        long batches = 0;
        await using (var c = OracleSql.Command(connection, "SELECT position, batches FROM bridge_checkpoint WHERE pipeline = :p"))
        {
            c.Parameters.Add("p", OracleDbType.Varchar2).Value = options.Pipeline;
            await using var r = await c.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct))
            {
                position = r.IsDBNull(0) ? null : r.GetString(0);
                batches = r.GetInt64(1);
            }
        }

        var parked = new Dictionary<string, long>(StringComparer.Ordinal);
        await using (var c = OracleSql.Command(connection, "SELECT reason, COUNT(*) FROM bridge_parked GROUP BY reason ORDER BY reason"))
        await using (var r = await c.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                parked[r.GetString(0)] = r.GetInt64(1);
            }
        }

        return new BridgeStatus(pending, oldest, position, batches, parked);
    }
}

/// <summary>Reads the whole source and the parking table as the bridge login, for end-to-end verification.</summary>
public sealed class SourceSnapshotReader(OracleSourceOptions options)
{
    private readonly SourceRowReader _rows = new(options.SourceSchema);

    public async Task<(IReadOnlyList<SourceRowState> Rows, IReadOnlySet<SourceKey> Parked)> ReadAsync(CancellationToken ct = default)
    {
        await using var connection = new OracleConnection(options.ConnectionString);
        await connection.OpenAsync(ct);
        var rows = new List<SourceRowState>();
        foreach (var table in new[] { SourceTable.Hasta, SourceTable.Basvuru, SourceTable.LabSonuc })
        {
            rows.AddRange(await _rows.ReadAllAsync(connection, table, ct));
        }

        var parked = new HashSet<SourceKey>();
        await using var command = OracleSql.Command(connection, "SELECT resource_type, source_id FROM bridge_parked");
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            parked.Add(new SourceKey(SourceTables.FromResourceType(reader.GetString(0)), reader.GetInt64(1)));
        }

        return (rows, parked);
    }
}
