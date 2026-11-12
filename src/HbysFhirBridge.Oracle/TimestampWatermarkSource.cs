using System.Globalization;
using HbysFhirBridge.Source;
using HbysFhirBridge.Sync;
using Oracle.ManagedDataAccess.Client;

namespace HbysFhirBridge.Oracle;

/// <summary>
/// The common "WHERE guncelleme_zamani &gt; :last_run" integration, kept only as a measured baseline (docs/benchmarks.md).
/// It has two structural defects the journal source does not: a stamp is taken when the statement runs but becomes
/// visible when the transaction commits, so a transaction that commits after a later-stamped one was read is skipped
/// for good; and a deleted row leaves nothing to select, so deletes never arrive. It does no parking (the experiment
/// uses clean data) and keeps its watermark in the checkpoint table.
/// </summary>
public sealed class TimestampWatermarkSource(OracleSourceOptions options) : IChangeSource
{
    private static readonly SourceTable[] Tables = [SourceTable.Hasta, SourceTable.Basvuru, SourceTable.LabSonuc];

    private readonly SourceRowReader _rows = new(options.SourceSchema);

    public async Task<IChangeBatch?> OpenBatchAsync(int maxChanges, CancellationToken cancellationToken)
    {
        var lease = await OracleJournalSource.Lease.AcquireAsync(options, cancellationToken);
        try
        {
            var watermarks = await ReadWatermarksAsync(lease.Connection, cancellationToken);
            var rows = new List<SourceRowState>();
            var next = new DateTime[Tables.Length];
            for (var i = 0; i < Tables.Length; i++)
            {
                var changed = await _rows.ReadStampedAfterAsync(lease.Connection, Tables[i], watermarks[i], cancellationToken);
                rows.AddRange(changed.Select(c => c.State));
                next[i] = changed.Count > 0 ? changed.Max(c => c.Stamp) : watermarks[i];
            }

            if (rows.Count == 0)
            {
                await lease.DisposeAsync();
                return null;
            }

            return new Batch(lease, rows, string.Join('|', next.Select(Format)));
        }
        catch
        {
            await lease.DisposeAsync();
            throw;
        }
    }

    public Task<IChangeBatch?> OpenUnblockedBatchAsync(IReadOnlyCollection<SourceKey> delivered, CancellationToken cancellationToken) =>
        Task.FromResult<IChangeBatch?>(null);

    private async Task<DateTime[]> ReadWatermarksAsync(OracleConnection connection, CancellationToken ct)
    {
        await using var command = OracleSql.Command(connection, "SELECT position FROM bridge_checkpoint WHERE pipeline = :p");
        command.Parameters.Add("p", OracleDbType.Varchar2).Value = options.Pipeline;
        var position = await command.ExecuteScalarAsync(ct) as string;
        return string.IsNullOrEmpty(position)
            ? Tables.Select(_ => DateTime.MinValue).ToArray()
            : position.Split('|').Select(p => DateTime.ParseExact(p, "O", CultureInfo.InvariantCulture)).ToArray();
    }

    private static string Format(DateTime value) => value.ToString("O", CultureInfo.InvariantCulture);

    private sealed class Batch(OracleJournalSource.Lease lease, IReadOnlyList<SourceRowState> rows, string position) : IChangeBatch
    {
        public IReadOnlyList<SourceRowState> Rows => rows;

        public DateTimeOffset? OldestChangeAt => null;

        public string Position => position;

        public Task<IReadOnlySet<SourceKey>> FindParkedAsync(IReadOnlyCollection<SourceKey> keys, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlySet<SourceKey>>(new HashSet<SourceKey>());

        public async Task CompleteAsync(IReadOnlyCollection<SourceKey> delivered, IReadOnlyCollection<ParkedRow> parked, CancellationToken cancellationToken)
        {
            await ParkingStore.MoveCheckpointAsync(lease.Connection, lease.Pipeline, position, cancellationToken);
            await lease.Transaction.CommitAsync(cancellationToken);
        }

        public ValueTask DisposeAsync() => lease.DisposeAsync();
    }
}
