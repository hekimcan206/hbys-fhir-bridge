using HbysFhirBridge.Source;
using Bundle = Hl7.Fhir.Model.Bundle;

namespace HbysFhirBridge.Sync;

/// <summary>
/// The source side of the bridge. One implementation reads the trigger-maintained change journal (the production
/// strategy); another reads "last updated" timestamps and exists only to measure what that approach loses.
/// </summary>
public interface IChangeSource
{
    /// <summary>Opens the next batch of changed rows, or returns null when nothing is pending.</summary>
    /// <remarks>
    /// An open batch is a unit of work: nothing is acknowledged until <see cref="IChangeBatch.CompleteAsync"/> runs,
    /// so a crash or an exception before that point re-delivers the same changes on the next run.
    /// </remarks>
    Task<IChangeBatch?> OpenBatchAsync(int maxChanges, CancellationToken cancellationToken);

    /// <summary>
    /// Opens a batch of parked rows that were waiting for one of <paramref name="delivered"/> (for example the
    /// Observations of a Patient that has just been fixed), or null when none are waiting.
    /// </summary>
    Task<IChangeBatch?> OpenUnblockedBatchAsync(IReadOnlyCollection<SourceKey> delivered, CancellationToken cancellationToken);
}

public interface IChangeBatch : IAsyncDisposable
{
    /// <summary>Distinct changed rows with their state at one consistent point (null row = deleted).</summary>
    IReadOnlyList<SourceRowState> Rows { get; }

    /// <summary>When the oldest change in the batch was committed to the journal, for lag metrics.</summary>
    DateTimeOffset? OldestChangeAt { get; }

    /// <summary>Opaque source position for logs and the checkpoint (an Oracle SCN or a timestamp).</summary>
    string Position { get; }

    /// <summary>Which of <paramref name="keys"/> are parked right now (outside this batch).</summary>
    Task<IReadOnlySet<SourceKey>> FindParkedAsync(IReadOnlyCollection<SourceKey> keys, CancellationToken cancellationToken);

    /// <summary>
    /// Atomically acknowledges the batch: consumes its journal entries, clears parking for delivered rows, records
    /// parked rows and moves the checkpoint. Disposing without completing rolls everything back.
    /// </summary>
    Task CompleteAsync(IReadOnlyCollection<SourceKey> delivered, IReadOnlyCollection<ParkedRow> parked, CancellationToken cancellationToken);
}

/// <summary>A row the bridge could not deliver, and why. <see cref="BlockedBy"/> is set when it waits on a parked parent.</summary>
public sealed record ParkedRow(SourceKey Key, string Reason, string Detail, SourceKey? BlockedBy);

public static class ParkReasons
{
    /// <summary>The resource failed local validation against the core spec or the project profiles.</summary>
    public const string Invalid = "invalid";

    /// <summary>The FHIR server rejected the resource (a 4xx for that entry alone).</summary>
    public const string Rejected = "rejected";

    /// <summary>The resource references a row that is itself parked.</summary>
    public const string Dependency = "dependency";
}

/// <summary>The FHIR server side: posts one transaction bundle.</summary>
public interface IFhirSink
{
    /// <summary>
    /// Sends a transaction. Returns accepted or rejected (a definitive 4xx answer about the content); transient
    /// failures (network, 5xx, 429) throw so that the batch is retried as a whole and nothing is acknowledged.
    /// </summary>
    Task<SinkResult> SendAsync(Bundle transaction, CancellationToken cancellationToken);
}

public sealed record SinkResult(bool Accepted, int StatusCode, string? Detail)
{
    public static SinkResult Ok(int statusCode = 200) => new(true, statusCode, null);

    public static SinkResult Reject(int statusCode, string detail) => new(false, statusCode, detail);
}
