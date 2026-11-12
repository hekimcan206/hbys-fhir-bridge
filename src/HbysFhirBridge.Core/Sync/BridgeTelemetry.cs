using System.Diagnostics;
using System.Diagnostics.Metrics;
using HbysFhirBridge.Bundling;
using HbysFhirBridge.Source;

namespace HbysFhirBridge.Sync;

/// <summary>
/// Traces and metrics for the sync loop. Names follow OpenTelemetry conventions; the worker registers both the
/// <see cref="ActivitySource"/> and the <see cref="Meter"/> with its OpenTelemetry pipeline.
/// </summary>
public static class BridgeTelemetry
{
    public const string Name = "HbysFhirBridge";

    public static readonly ActivitySource Source = new(Name);

    private static readonly Meter Meter = new(Name);

    private static readonly Counter<long> Delivered = Meter.CreateCounter<long>(
        "bridge.resources.delivered", "{resource}", "Resources accepted by the FHIR server");

    private static readonly Counter<long> Parked = Meter.CreateCounter<long>(
        "bridge.resources.parked", "{resource}", "Rows parked instead of delivered");

    private static readonly Counter<long> Bundles = Meter.CreateCounter<long>(
        "bridge.bundles.sent", "{bundle}", "Transaction bundles posted, including bisecting retries");

    private static readonly Histogram<double> BatchDuration = Meter.CreateHistogram<double>(
        "bridge.batch.duration", "s", "Time to map, validate, send and acknowledge one batch");

    private static readonly Histogram<double> Lag = Meter.CreateHistogram<double>(
        "bridge.change.lag", "s", "Oldest change in a batch: journal commit to acknowledged delivery");

    internal static void RecordBatch(
        IChangeBatch batch,
        IReadOnlyCollection<OutboundChange> delivered,
        IEnumerable<ParkedRow> parked,
        int bundles,
        TimeSpan elapsed)
    {
        foreach (var group in delivered.GroupBy(d => (Type: SourceTables.ToResourceType(d.Key.Table), d.IsDelete)))
        {
            Delivered.Add(group.Count(),
                new KeyValuePair<string, object?>("fhir.resource_type", group.Key.Type),
                new KeyValuePair<string, object?>("bridge.operation", group.Key.IsDelete ? "delete" : "upsert"));
        }

        foreach (var group in parked.GroupBy(p => p.Reason))
        {
            Parked.Add(group.Count(), new KeyValuePair<string, object?>("bridge.reason", group.Key));
        }

        Bundles.Add(bundles);
        BatchDuration.Record(elapsed.TotalSeconds);
        if (batch.OldestChangeAt is { } oldest)
        {
            Lag.Record(Math.Max(0, (DateTimeOffset.UtcNow - oldest).TotalSeconds));
        }
    }
}
