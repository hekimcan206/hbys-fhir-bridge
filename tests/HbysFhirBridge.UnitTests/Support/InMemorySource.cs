using HbysFhirBridge.Source;
using HbysFhirBridge.Sync;

namespace HbysFhirBridge.UnitTests.Support;

/// <summary>
/// An in-memory model of the Oracle journal source with the same contract: a batch sees a snapshot, nothing is
/// acknowledged until CompleteAsync, and parking (with blocked-by) lives next to the journal.
/// </summary>
internal sealed class InMemorySource : IChangeSource
{
    private readonly Dictionary<SourceKey, object> _rows = [];
    private readonly List<(long Id, SourceKey Key)> _journal = [];
    private long _nextJournalId;

    public Dictionary<SourceKey, ParkedRow> Parked { get; } = [];

    /// <summary>Set to make the next CompleteAsync throw (a crash after delivery, before acknowledgement).</summary>
    public bool FailNextComplete { get; set; }

    public int Completed { get; private set; }

    public IReadOnlyDictionary<SourceKey, object> Rows => _rows;

    public int Pending => _journal.Count;

    public void Upsert(SourceKey key, object row)
    {
        _rows[key] = row;
        _journal.Add((++_nextJournalId, key));
    }

    public void Delete(SourceKey key)
    {
        _rows.Remove(key);
        _journal.Add((++_nextJournalId, key));
    }

    public Task<IChangeBatch?> OpenBatchAsync(int maxChanges, CancellationToken cancellationToken)
    {
        if (_journal.Count == 0)
        {
            return Task.FromResult<IChangeBatch?>(null);
        }

        var entries = _journal.OrderBy(j => j.Id).Take(maxChanges).ToList();
        var states = entries.Select(e => e.Key).Distinct().Select(k => new SourceRowState(k, _rows.GetValueOrDefault(k))).ToList();
        return Task.FromResult<IChangeBatch?>(new Batch(this, states, entries.Select(e => e.Id).ToHashSet()));
    }

    public Task<IChangeBatch?> OpenUnblockedBatchAsync(IReadOnlyCollection<SourceKey> delivered, CancellationToken cancellationToken)
    {
        var set = delivered.ToHashSet();
        var waiting = Parked.Values.Where(p => p.BlockedBy is { } b && set.Contains(b)).Select(p => p.Key).ToList();
        if (waiting.Count == 0)
        {
            return Task.FromResult<IChangeBatch?>(null);
        }

        var states = waiting.Select(k => new SourceRowState(k, _rows.GetValueOrDefault(k))).ToList();
        return Task.FromResult<IChangeBatch?>(new Batch(this, states, []));
    }

    private sealed class Batch(InMemorySource owner, IReadOnlyList<SourceRowState> rows, HashSet<long> journalIds) : IChangeBatch
    {
        public IReadOnlyList<SourceRowState> Rows => rows;

        public DateTimeOffset? OldestChangeAt => null;

        public string Position => $"mem:{journalIds.DefaultIfEmpty().Max()}";

        public Task<IReadOnlySet<SourceKey>> FindParkedAsync(IReadOnlyCollection<SourceKey> keys, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlySet<SourceKey>>(keys.Where(owner.Parked.ContainsKey).ToHashSet());

        public Task CompleteAsync(IReadOnlyCollection<SourceKey> delivered, IReadOnlyCollection<ParkedRow> parked, CancellationToken cancellationToken)
        {
            if (owner.FailNextComplete)
            {
                owner.FailNextComplete = false;
                throw new InvalidOperationException("simulated crash before acknowledgement");
            }

            owner._journal.RemoveAll(j => journalIds.Contains(j.Id));
            foreach (var key in delivered)
            {
                owner.Parked.Remove(key);
            }

            foreach (var p in parked)
            {
                owner.Parked[p.Key] = p;
            }

            owner.Completed++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
