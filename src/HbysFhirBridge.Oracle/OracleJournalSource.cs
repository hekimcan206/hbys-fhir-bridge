using System.Globalization;
using System.Text;
using HbysFhirBridge.Source;
using HbysFhirBridge.Sync;
using Oracle.ManagedDataAccess.Client;

namespace HbysFhirBridge.Oracle;

/// <summary>Thrown when another bridge instance holds the pipeline lease (its batch is in flight).</summary>
public sealed class PipelineBusyException(string pipeline)
    : Exception($"Pipeline '{pipeline}' is being processed by another bridge instance");

public sealed class OracleSourceOptions
{
    /// <summary>The bridge login (read-only on clinical data).</summary>
    public required string ConnectionString { get; init; }

    /// <summary>The schema that owns the HBYS tables and the journal.</summary>
    public string SourceSchema { get; init; } = "HBYS";

    public string Pipeline { get; init; } = "fhir";
}

/// <summary>
/// The production change source: the trigger-maintained journal, consumed like a queue.
/// <list type="number">
/// <item>Lock the pipeline's checkpoint row (single-writer lease) and read the current SCN.</item>
/// <item>Read up to N journal entries AS OF that SCN, and the changed rows AS OF the same SCN.</item>
/// <item>After delivery, delete exactly the entries that were read, record parking and commit, all in one transaction.</item>
/// </list>
/// An entry committed after the SCN is invisible to this batch and still in the table for the next one, which is
/// why this strategy cannot lose a change to a long-running transaction (docs/adr/0002).
/// </summary>
public sealed class OracleJournalSource(OracleSourceOptions options) : IChangeSource
{
    private readonly string _schema = OracleSql.Identifier(options.SourceSchema);
    private readonly SourceRowReader _rows = new(options.SourceSchema);

    public async Task<IChangeBatch?> OpenBatchAsync(int maxChanges, CancellationToken cancellationToken)
    {
        var lease = await Lease.AcquireAsync(options, cancellationToken);
        try
        {
            var journal = new List<(long Id, SourceKey Key, DateTime At)>();
            await using (var command = OracleSql.Command(lease.Connection,
                $"SELECT gunluk_id, tablo_adi, kayit_id, islem_zamani FROM {_schema}.degisiklik_gunlugu AS OF SCN :scn " +
                "ORDER BY gunluk_id FETCH FIRST :n ROWS ONLY"))
            {
                command.Parameters.Add("scn", OracleDbType.Int64).Value = lease.Scn;
                command.Parameters.Add("n", OracleDbType.Int32).Value = maxChanges;
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    journal.Add((reader.GetInt64(0), new SourceKey(SourceTables.FromDbName(reader.GetString(1)), reader.GetInt64(2)),
                        reader.GetDateTime(3)));
                }
            }

            if (journal.Count == 0)
            {
                await lease.DisposeAsync();
                return null;
            }

            var keys = journal.Select(j => j.Key).Distinct().ToList();
            var rows = await _rows.ReadAsync(lease.Connection, keys, lease.Scn, cancellationToken);
            var oldest = new DateTimeOffset(DateTime.SpecifyKind(journal.Min(j => j.At), DateTimeKind.Unspecified), lease.SessionOffset);
            return new Batch(lease, _schema, rows, journal.Select(j => j.Id).ToList(), oldest);
        }
        catch
        {
            await lease.DisposeAsync();
            throw;
        }
    }

    public async Task<IChangeBatch?> OpenUnblockedBatchAsync(IReadOnlyCollection<SourceKey> delivered, CancellationToken cancellationToken)
    {
        var parents = delivered.Where(k => k.Table != SourceTable.LabSonuc).ToList();
        if (parents.Count == 0)
        {
            return null;
        }

        var lease = await Lease.AcquireAsync(options, cancellationToken);
        try
        {
            var waiting = new List<SourceKey>();
            foreach (var group in parents.GroupBy(p => p.Table))
            {
                foreach (var chunk in group.Select(k => k.Id).Chunk(OracleSql.InListChunk))
                {
                    var sql = new StringBuilder("SELECT resource_type, source_id FROM bridge_parked WHERE blocked_type = :bt AND blocked_id IN (");
                    await using var command = OracleSql.Command(lease.Connection, "");
                    command.Parameters.Add("bt", OracleDbType.Varchar2).Value = SourceTables.ToResourceType(group.Key);
                    OracleSql.AppendInList(sql, command, chunk);
                    command.CommandText = sql.Append(')').ToString();
                    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        waiting.Add(new SourceKey(SourceTables.FromResourceType(reader.GetString(0)), reader.GetInt64(1)));
                    }
                }
            }

            if (waiting.Count == 0)
            {
                await lease.DisposeAsync();
                return null;
            }

            var rows = await _rows.ReadAsync(lease.Connection, waiting, lease.Scn, cancellationToken);
            return new Batch(lease, _schema, rows, [], null);
        }
        catch
        {
            await lease.DisposeAsync();
            throw;
        }
    }

    /// <summary>An open connection and transaction holding the pipeline row lock, plus the batch SCN.</summary>
    internal sealed class Lease : IAsyncDisposable
    {
        private Lease(OracleConnection connection, OracleTransaction transaction, string pipeline, long scn, TimeSpan offset)
        {
            Connection = connection;
            Transaction = transaction;
            Pipeline = pipeline;
            Scn = scn;
            SessionOffset = offset;
        }

        public OracleConnection Connection { get; }

        public OracleTransaction Transaction { get; }

        public string Pipeline { get; }

        public long Scn { get; }

        /// <summary>UTC offset of the database clock, used to turn TIMESTAMP journal times into instants.</summary>
        public TimeSpan SessionOffset { get; }

        public static async Task<Lease> AcquireAsync(OracleSourceOptions options, CancellationToken ct)
        {
            var connection = new OracleConnection(options.ConnectionString);
            try
            {
                await connection.OpenAsync(ct);
                await using (var ensure = OracleSql.Command(connection,
                    "MERGE INTO bridge_checkpoint c USING (SELECT :p pipeline FROM dual) s ON (c.pipeline = s.pipeline) " +
                    "WHEN NOT MATCHED THEN INSERT (pipeline) VALUES (s.pipeline)"))
                {
                    ensure.Parameters.Add("p", OracleDbType.Varchar2).Value = options.Pipeline;
                    await ensure.ExecuteNonQueryAsync(ct); // autocommit: the row must exist before it can be locked
                }

                var transaction = connection.BeginTransaction();
                try
                {
                    await using (var lockRow = OracleSql.Command(connection,
                        "SELECT position FROM bridge_checkpoint WHERE pipeline = :p FOR UPDATE NOWAIT"))
                    {
                        lockRow.Parameters.Add("p", OracleDbType.Varchar2).Value = options.Pipeline;
                        try
                        {
                            await lockRow.ExecuteScalarAsync(ct);
                        }
                        catch (OracleException ex) when (ex.Number == 54)
                        {
                            throw new PipelineBusyException(options.Pipeline);
                        }
                    }

                    // The SCN is read after the lock, so it is at least as new as the previous batch's commit.
                    await using var scnCommand = OracleSql.Command(connection,
                        "SELECT DBMS_FLASHBACK.GET_SYSTEM_CHANGE_NUMBER, " +
                        "EXTRACT(TIMEZONE_HOUR FROM SYSTIMESTAMP) * 60 + EXTRACT(TIMEZONE_MINUTE FROM SYSTIMESTAMP) FROM dual");
                    await using var reader = await scnCommand.ExecuteReaderAsync(ct);
                    await reader.ReadAsync(ct);
                    var scn = reader.GetInt64(0);
                    var offset = TimeSpan.FromMinutes(reader.GetInt32(1));
                    return new Lease(connection, transaction, options.Pipeline, scn, offset);
                }
                catch
                {
                    await transaction.DisposeAsync();
                    throw;
                }
            }
            catch
            {
                await connection.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            // Rolls back unless CompleteAsync committed: the journal entries stay and are re-delivered.
            await Transaction.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }

    private sealed class Batch(Lease lease, string schema, IReadOnlyList<SourceRowState> rows, IReadOnlyList<long> journalIds, DateTimeOffset? oldest)
        : IChangeBatch
    {
        public IReadOnlyList<SourceRowState> Rows => rows;

        public DateTimeOffset? OldestChangeAt => oldest;

        public string Position => "scn:" + lease.Scn.ToString(CultureInfo.InvariantCulture);

        public Task<IReadOnlySet<SourceKey>> FindParkedAsync(IReadOnlyCollection<SourceKey> keys, CancellationToken cancellationToken) =>
            ParkingStore.FindParkedAsync(lease.Connection, keys, cancellationToken);

        public async Task CompleteAsync(IReadOnlyCollection<SourceKey> delivered, IReadOnlyCollection<ParkedRow> parked, CancellationToken cancellationToken)
        {
            if (journalIds.Count > 0)
            {
                await using var consume = OracleSql.Command(lease.Connection,
                    $"DELETE FROM {schema}.degisiklik_gunlugu WHERE gunluk_id = :id");
                consume.ArrayBindCount = journalIds.Count;
                consume.Parameters.Add("id", OracleDbType.Int64).Value = journalIds.ToArray();
                await consume.ExecuteNonQueryAsync(cancellationToken);
            }

            await ParkingStore.ApplyAsync(lease.Connection, delivered, parked, cancellationToken);
            await ParkingStore.MoveCheckpointAsync(lease.Connection, lease.Pipeline, Position, cancellationToken);
            await lease.Transaction.CommitAsync(cancellationToken);
        }

        public ValueTask DisposeAsync() => lease.DisposeAsync();
    }
}
