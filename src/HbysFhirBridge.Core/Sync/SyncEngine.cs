using System.Diagnostics;
using HbysFhirBridge.Bundling;
using HbysFhirBridge.Mapping;
using HbysFhirBridge.Source;
using HbysFhirBridge.Validation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HbysFhirBridge.Sync;

public sealed class SyncOptions
{
    /// <summary>Journal entries per batch (one source transaction, one checkpoint move).</summary>
    public int BatchSize { get; set; } = 500;

    /// <summary>Entries per transaction bundle; a batch larger than this is sent as several bundles in order.</summary>
    public int MaxBundleEntries { get; set; } = 250;

    /// <summary>Upper bound on batches per <see cref="SyncEngine.DrainAsync"/> call, so one call cannot run forever.</summary>
    public int MaxBatchesPerDrain { get; set; } = 10_000;

    /// <summary>
    /// Hold a child (Encounter, Observation) back without sending it when the row it references is parked, instead
    /// of letting the server reject it and bisecting to find it. On by default; off only to measure the cost of
    /// discovering every dependency through 409 answers (docs/benchmarks.md).
    /// </summary>
    public bool HoldChildrenOfParkedParents { get; set; } = true;
}

/// <summary>What one batch did.</summary>
public sealed record BatchReport(
    string Position,
    int Rows,
    int Upserts,
    int Deletes,
    IReadOnlyList<ParkedRow> Parked,
    int BundlesSent,
    TimeSpan Elapsed)
{
    public int Delivered => Upserts + Deletes;

    public IReadOnlyList<SourceKey> DeliveredKeys { get; init; } = [];
}

public sealed record SyncReport(int Batches, int Rows, int Delivered, int Parked, int BundlesSent, TimeSpan Elapsed)
{
    public static SyncReport Empty { get; } = new(0, 0, 0, 0, 0, TimeSpan.Zero);

    public SyncReport Add(BatchReport batch) => new(
        Batches + 1, Rows + batch.Rows, Delivered + batch.Delivered, Parked + batch.Parked.Count,
        BundlesSent + batch.BundlesSent, Elapsed + batch.Elapsed);
}

/// <summary>
/// Moves changed source rows to the FHIR server: map, validate, send as idempotent transaction bundles, park what
/// cannot be delivered, acknowledge. The engine holds no state between batches; all of it lives in the source
/// (journal, parking table, checkpoint) so a restart is always safe.
/// </summary>
public sealed partial class SyncEngine(
    IChangeSource source,
    IFhirSink sink,
    FhirMapper mapper,
    IResourceValidator validator,
    SyncOptions options,
    ILogger<SyncEngine>? logger = null)
{
    // Patient -> Encounter -> Observation: at most two levels of waiting children can be released by one delivery.
    private const int MaxUnblockDepth = 3;

    private readonly ILogger _logger = logger ?? NullLogger<SyncEngine>.Instance;

    /// <summary>Processes batches until the journal is empty (or the per-call cap is reached).</summary>
    public async Task<SyncReport> DrainAsync(CancellationToken cancellationToken = default)
    {
        var report = SyncReport.Empty;
        for (var i = 0; i < options.MaxBatchesPerDrain; i++)
        {
            var batch = await ProcessNextBatchAsync(cancellationToken);
            if (batch is null)
            {
                break;
            }

            report = report.Add(batch);
        }

        return report;
    }

    /// <summary>Processes one journal batch plus any parked rows it releases; null when the journal is empty.</summary>
    public async Task<BatchReport?> ProcessNextBatchAsync(CancellationToken cancellationToken = default)
    {
        BatchReport? report;
        await using (var batch = await source.OpenBatchAsync(options.BatchSize, cancellationToken))
        {
            if (batch is null)
            {
                return null;
            }

            report = await ProcessAsync(batch, cancellationToken);
        }

        var released = report.Delivered > 0 ? await ReleaseWaitingAsync(report, cancellationToken) : null;
        return released is null ? report : Merge(report, released);
    }

    private async Task<BatchReport?> ReleaseWaitingAsync(BatchReport parent, CancellationToken cancellationToken)
    {
        BatchReport? total = null;
        var delivered = parent.DeliveredKeys;
        for (var depth = 0; depth < MaxUnblockDepth && delivered.Count > 0; depth++)
        {
            BatchReport step;
            await using (var batch = await source.OpenUnblockedBatchAsync(delivered, cancellationToken))
            {
                if (batch is null)
                {
                    break;
                }

                step = await ProcessAsync(batch, cancellationToken);
            }

            LogReleased(step.Delivered, step.Parked.Count);
            total = total is null ? step : Merge(total, step);
            delivered = step.DeliveredKeys;
        }

        return total;
    }

    private async Task<BatchReport> ProcessAsync(IChangeBatch batch, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        using var activity = BridgeTelemetry.Source.StartActivity("bridge.batch");
        activity?.SetTag("bridge.position", batch.Position);
        activity?.SetTag("bridge.rows", batch.Rows.Count);

        var parked = new Dictionary<SourceKey, ParkedRow>();
        var outbound = new List<OutboundChange>(batch.Rows.Count);
        var rowsByKey = new Dictionary<SourceKey, object>();

        foreach (var state in batch.Rows)
        {
            if (state.Row is null)
            {
                outbound.Add(new OutboundChange(state.Key, null));
                continue;
            }

            rowsByKey[state.Key] = state.Row;
            var mapped = mapper.Map(state.Key, state.Row);
            if (!mapped.Succeeded)
            {
                parked[state.Key] = new ParkedRow(state.Key, mapped.FailureReason!, mapped.FailureDetail!, null);
                continue;
            }

            var validation = validator.Validate(mapped.Resource!);
            if (!validation.IsValid)
            {
                parked[state.Key] = new ParkedRow(state.Key, ParkReasons.Invalid, validation.Describe(), null);
                continue;
            }

            outbound.Add(new OutboundChange(state.Key, mapped.Resource));
        }

        if (options.HoldChildrenOfParkedParents)
        {
            outbound = await HoldChildrenOfParkedParentsAsync(batch, outbound, rowsByKey, parked, cancellationToken);
        }

        var delivered = new List<OutboundChange>(outbound.Count);
        var rejected = new List<(OutboundChange Change, SinkResult Result)>();
        var bundles = 0;
        foreach (var chunk in TransactionBundleBuilder.Order(outbound).Chunk(Math.Max(1, options.MaxBundleEntries)))
        {
            bundles += await DeliverAsync(chunk, delivered, rejected, cancellationToken);
        }

        foreach (var (change, result) in rejected)
        {
            parked[change.Key] = await ClassifyRejectionAsync(batch, change, result, rowsByKey, parked, cancellationToken);
        }

        var deliveredKeys = delivered.Select(d => d.Key).ToList();
        await batch.CompleteAsync(deliveredKeys, parked.Values.ToList(), cancellationToken);

        var upserts = delivered.Count(d => !d.IsDelete);
        var deletes = delivered.Count - upserts;
        BridgeTelemetry.RecordBatch(batch, delivered, parked.Values, bundles, Stopwatch.GetElapsedTime(started));
        foreach (var p in parked.Values)
        {
            LogParked(p.Key.ToString(), p.Reason, p.Detail);
        }

        return new BatchReport(batch.Position, batch.Rows.Count, upserts, deletes, parked.Values.ToList(), bundles,
            Stopwatch.GetElapsedTime(started))
        {
            DeliveredKeys = deliveredKeys,
        };
    }

    /// <summary>
    /// Sends a run of entries as one transaction. A rejection does not say which entry was at fault, so the run is
    /// split in half and each half is retried, recursively, until the offending entries stand alone. One bad
    /// resource costs about 2·log2(n) extra round trips instead of blocking the other n-1 (docs/adr/0003).
    /// </summary>
    private async Task<int> DeliverAsync(
        IReadOnlyList<OutboundChange> run,
        List<OutboundChange> delivered,
        List<(OutboundChange, SinkResult)> rejected,
        CancellationToken cancellationToken)
    {
        if (run.Count == 0)
        {
            return 0;
        }

        var result = await sink.SendAsync(TransactionBundleBuilder.Build(run), cancellationToken);
        if (result.Accepted)
        {
            delivered.AddRange(run);
            return 1;
        }

        if (run.Count == 1)
        {
            rejected.Add((run[0], result));
            return 1;
        }

        var half = run.Count / 2;
        var sent = 1;
        sent += await DeliverAsync(run.Take(half).ToList(), delivered, rejected, cancellationToken);
        sent += await DeliverAsync(run.Skip(half).ToList(), delivered, rejected, cancellationToken);
        return sent;
    }

    private static async Task<ParkedRow> ClassifyRejectionAsync(
        IChangeBatch batch,
        OutboundChange change,
        SinkResult result,
        Dictionary<SourceKey, object> rowsByKey,
        Dictionary<SourceKey, ParkedRow> parkedInBatch,
        CancellationToken cancellationToken)
    {
        var detail = $"HTTP {result.StatusCode}: {result.Detail}";
        if (!rowsByKey.TryGetValue(change.Key, out var row))
        {
            return new ParkedRow(change.Key, ParkReasons.Rejected, detail, null);
        }

        // A child rejected because its parent is parked is waiting, not broken: remember the parent so that the
        // child is retried the moment the parent is delivered.
        var references = SourceReferences.Of(row);
        var waitingOn = references.FirstOrDefault(parkedInBatch.ContainsKey);
        if (waitingOn == default && references.Count > 0)
        {
            var parkedElsewhere = await batch.FindParkedAsync(references, cancellationToken);
            waitingOn = references.FirstOrDefault(parkedElsewhere.Contains);
        }

        return waitingOn == default
            ? new ParkedRow(change.Key, ParkReasons.Rejected, detail, null)
            : new ParkedRow(change.Key, ParkReasons.Dependency, $"waits for {waitingOn}; {detail}", waitingOn);
    }

    /// <summary>
    /// Parks upserts whose parent is parked, parents first so that a held Encounter also holds its Observations.
    /// A parent that is itself being sent in this batch does not hold anything back: if the server rejects it, the
    /// bisecting path finds the children.
    /// </summary>
    private static async Task<List<OutboundChange>> HoldChildrenOfParkedParentsAsync(
        IChangeBatch batch,
        List<OutboundChange> outbound,
        Dictionary<SourceKey, object> rowsByKey,
        Dictionary<SourceKey, ParkedRow> parked,
        CancellationToken cancellationToken)
    {
        var inBatch = batch.Rows.Select(r => r.Key).ToHashSet();
        var outside = outbound
            .Where(o => !o.IsDelete)
            .SelectMany(o => SourceReferences.Of(rowsByKey[o.Key]))
            .Where(r => !inBatch.Contains(r))
            .Distinct()
            .ToList();
        var parkedOutside = outside.Count > 0 ? await batch.FindParkedAsync(outside, cancellationToken) : new HashSet<SourceKey>();

        var kept = new List<OutboundChange>(outbound.Count);
        foreach (var change in outbound.OrderBy(o => SourceTables.DependencyRank(o.Key.Table)))
        {
            if (!change.IsDelete)
            {
                var waitingOn = SourceReferences.Of(rowsByKey[change.Key])
                    .FirstOrDefault(r => parked.ContainsKey(r) || parkedOutside.Contains(r));
                if (waitingOn != default)
                {
                    parked[change.Key] = new ParkedRow(change.Key, ParkReasons.Dependency, $"waits for {waitingOn} (parked)", waitingOn);
                    continue;
                }
            }

            kept.Add(change);
        }

        return kept;
    }

    private static BatchReport Merge(BatchReport a, BatchReport b) => new(
        a.Position, a.Rows + b.Rows, a.Upserts + b.Upserts, a.Deletes + b.Deletes, [.. a.Parked, .. b.Parked],
        a.BundlesSent + b.BundlesSent, a.Elapsed + b.Elapsed)
    {
        DeliveredKeys = [.. a.DeliveredKeys, .. b.DeliveredKeys],
    };

    [LoggerMessage(Level = LogLevel.Warning, Message = "Parked {Key} ({Reason}): {Detail}")]
    private partial void LogParked(string key, string reason, string detail);

    [LoggerMessage(Level = LogLevel.Information, Message = "Released {Delivered} waiting rows ({Parked} still parked)")]
    private partial void LogReleased(int delivered, int parked);
}
