using System.Diagnostics;
using System.Globalization;
using HbysFhirBridge.Fhir;
using HbysFhirBridge.Mapping;
using HbysFhirBridge.Oracle;
using HbysFhirBridge.Source;
using HbysFhirBridge.Sync;
using HbysFhirBridge.Synthetic;
using HbysFhirBridge.Validation;
using Oracle.ManagedDataAccess.Client;

namespace HbysFhirBridge.Benchmarks;

/// <summary>
/// End-to-end experiments against a real Oracle (HFB_OWNER, HFB_BRIDGE connection strings) and, for "e2e", a fresh
/// Blaze server (HFB_FHIR). Each prints a markdown table; docs/benchmarks.md quotes the output verbatim.
/// </summary>
internal sealed class Experiments(string owner, string bridge, ResourceValidator validator)
{
    private readonly FhirMapper _mapper = new();

    private OracleSourceOptions Source(string pipeline) => new() { ConnectionString = bridge, Pipeline = pipeline };

    private SyncEngine Engine(IChangeSource source, IFhirSink sink, bool hold = true) =>
        new(source, sink, _mapper, validator, new SyncOptions { HoldChildrenOfParkedParents = hold });

    /// <summary>
    /// Capture correctness under concurrent writers: the journal source and the timestamp source run side by side
    /// against the same workload, each into an in-memory sink; afterwards each sink is compared with the source.
    /// </summary>
    public async Task CaptureAsync(int patients, int writers, TimeSpan duration, TimeSpan maxHold, TimeSpan poll, int runs)
    {
        Console.WriteLine($"capture: {patients} patients, {writers} writers x {duration.TotalSeconds:F0} s, hold 0-{maxHold.TotalMilliseconds:F0} ms, poll {poll.TotalMilliseconds:F0} ms");
        Console.WriteLine("| run | rows changed | journal: stale / missing / not deleted | timestamp: stale / missing / not deleted |");
        Console.WriteLine("|---|---|---|---|");
        for (var run = 1; run <= runs; run++)
        {
            await ResetAsync();
            await OracleSeeder.SeedAsync(owner, SyntheticHospital.Generate(new SyntheticOptions { Patients = patients, DirtyRate = 0, Seed = run }));
            var journalSink = new InMemoryFhirSink();
            var naiveSink = new InMemoryFhirSink();
            var journal = Engine(new OracleJournalSource(Source("fhir")), journalSink);
            var naive = Engine(new TimestampWatermarkSource(Source("naive")), naiveSink);
            await journal.DrainAsync();
            await naive.DrainAsync();

            using var stop = new CancellationTokenSource(duration);
            var changed = 0;
            var writerTasks = Enumerable.Range(0, writers).Select(async w =>
            {
                await using var workload = await SourceWorkload.OpenAsync(owner, new WorkloadOptions { Seed = (run * 100) + w, MaxHold = maxHold });
                while (!stop.IsCancellationRequested)
                {
                    var (_, rows) = await workload.StepAsync();
                    Interlocked.Add(ref changed, rows);
                }
            }).ToList();
            var pollers = new[] { journal, naive }.Select(async engine =>
            {
                while (!stop.IsCancellationRequested)
                {
                    await engine.DrainAsync();
                    await Task.Delay(poll);
                }
            }).ToList();
            await Task.WhenAll(writerTasks.Concat(pollers));
            await journal.DrainAsync();
            await naive.DrainAsync();

            var expected = await ExpectedStateAsync();
            var j = Compare(journalSink, expected);
            var n = Compare(naiveSink, expected);
            Console.WriteLine($"| {run} | {changed} | {j.Stale} / {j.Missing} / {j.NotDeleted} | {n.Stale} / {n.Missing} / {n.NotDeleted} |");
        }
    }

    /// <summary>Initial load, replay and live incremental sync against Blaze.</summary>
    public async Task EndToEndAsync(Uri fhir, int patients, double dirtyRate, bool hold, int liveSeconds, int opsPerSecond)
    {
        using var http = new HttpClient { BaseAddress = fhir, Timeout = TimeSpan.FromMinutes(5) };
        var sink = new FhirHttpSink(http);
        var reader = new FhirReadClient(http);
        await ResetAsync();

        var dataset = SyntheticHospital.Generate(new SyntheticOptions { Patients = patients, DirtyRate = dirtyRate });
        var sw = Stopwatch.StartNew();
        await OracleSeeder.SeedAsync(owner, dataset);
        var seedTime = sw.Elapsed;

        var lag = new List<double>();
        var source = new LagRecordingSource(new OracleJournalSource(Source("fhir")), lag);
        sw.Restart();
        var load = await Engine(source, sink, hold).DrainAsync();
        var loadTime = sw.Elapsed;
        Console.WriteLine($"e2e: {patients} patients, dirty rate {dirtyRate:P0}, hold children = {hold}");
        Console.WriteLine("| step | rows | delivered | parked | bundles | seconds | rows/s |");
        Console.WriteLine("|---|---|---|---|---|---|---|");
        Console.WriteLine($"| seed ({dataset.RowCount} rows, array binding) | {dataset.RowCount} | - | - | - | {seedTime.TotalSeconds:F1} | {dataset.RowCount / seedTime.TotalSeconds:F0} |");
        Console.WriteLine(Row("initial load", load, loadTime));

        var historyBefore = await HistoryAsync(reader);
        await ExecuteAsync(owner, "UPDATE hasta SET telefon = telefon", "UPDATE basvuru SET durum = durum", "UPDATE lab_sonuc SET durum = durum");
        sw.Restart();
        var replay = await Engine(source, sink, hold).DrainAsync();
        var replayTime = sw.Elapsed;
        var historyAfter = await HistoryAsync(reader);
        Console.WriteLine(Row("replay of every row (no-op updates)", replay, replayTime));
        Console.WriteLine($"\nreplay wrote {historyAfter - historyBefore} new versions on the server (history {historyBefore} -> {historyAfter})");

        if (liveSeconds <= 0)
        {
            return;
        }

        lag.Clear();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(liveSeconds));
        var engine = Engine(source, sink, hold);
        var delivered = 0;
        var writer = Task.Run(async () =>
        {
            await using var workload = await SourceWorkload.OpenAsync(owner);
            var interval = TimeSpan.FromSeconds(1.0 / opsPerSecond);
            var clock = Stopwatch.StartNew();
            for (var i = 1; !stop.IsCancellationRequested; i++)
            {
                await workload.StepAsync();
                var due = interval * i;
                if (due > clock.Elapsed)
                {
                    await Task.Delay(due - clock.Elapsed);
                }
            }
        });
        var poller = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                delivered += (await engine.DrainAsync()).Delivered;
                await Task.Delay(500);
            }
        });
        await Task.WhenAll(writer, poller);
        delivered += (await engine.DrainAsync()).Delivered;
        lag.Sort();
        Console.WriteLine($"\nlive: {opsPerSecond} writes/s for {liveSeconds} s, poll 500 ms -> {delivered} delivered; " +
                          $"oldest-change lag per batch p50 {Percentile(lag, 0.5):F2} s, p95 {Percentile(lag, 0.95):F2} s, max {lag.LastOrDefault():F2} s ({lag.Count} batches)");
    }

    private static string Row(string name, SyncReport r, TimeSpan t) =>
        $"| {name} | {r.Rows} | {r.Delivered} | {r.Parked} | {r.BundlesSent} | {t.TotalSeconds:F1} | {r.Rows / t.TotalSeconds:F0} |";

    private static double Percentile(List<double> sorted, double p) =>
        sorted.Count == 0 ? double.NaN : sorted[(int)Math.Min(sorted.Count - 1, Math.Ceiling(p * sorted.Count) - 1)];

    private static async Task<int> HistoryAsync(FhirReadClient reader) =>
        await reader.HistoryCountAsync("Patient") + await reader.HistoryCountAsync("Encounter") + await reader.HistoryCountAsync("Observation");

    private async Task<Dictionary<string, string>> ExpectedStateAsync()
    {
        var (rows, _) = await new SourceSnapshotReader(Source("fhir")).ReadAsync();
        return rows.ToDictionary(r => ResourceIds.Reference(r.Key.Table, r.Key.Id), r => FhirJson.Serialize(_mapper.Map(r.Key, r.Row!).Resource!));
    }

    private static (int Stale, int Missing, int NotDeleted) Compare(InMemoryFhirSink sink, Dictionary<string, string> expected)
    {
        int stale = 0, missing = 0, notDeleted = 0;
        foreach (var (url, json) in expected)
        {
            if (!sink.State.TryGetValue(url, out var actual) || actual is null)
            {
                missing++;
            }
            else if (actual != json)
            {
                stale++;
            }
        }

        notDeleted += sink.State.Count(s => s.Value is not null && !expected.ContainsKey(s.Key));
        return (stale, missing, notDeleted);
    }

    private async Task ResetAsync()
    {
        await ExecuteAsync(owner, "DELETE FROM lab_sonuc", "DELETE FROM basvuru", "DELETE FROM hasta");
        await ExecuteAsync(bridge, "DELETE FROM hbys.degisiklik_gunlugu", "DELETE FROM bridge_parked", "DELETE FROM bridge_checkpoint");
    }

    private static async Task ExecuteAsync(string connectionString, params string[] statements)
    {
        await using var connection = new OracleConnection(connectionString);
        await connection.OpenAsync();
        foreach (var sql in statements)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }
    }

    public static string Arg(string[] args, string name, string fallback)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
    }

    public static int IntArg(string[] args, string name, int fallback) => int.Parse(Arg(args, name, fallback.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture);
}

/// <summary>Records, per completed journal batch, how long its oldest change waited (journal commit to acknowledgement).</summary>
internal sealed class LagRecordingSource(IChangeSource inner, List<double> lag) : IChangeSource
{
    public async Task<IChangeBatch?> OpenBatchAsync(int maxChanges, CancellationToken cancellationToken) =>
        await inner.OpenBatchAsync(maxChanges, cancellationToken) is { } batch ? new Batch(batch, lag) : null;

    public Task<IChangeBatch?> OpenUnblockedBatchAsync(IReadOnlyCollection<SourceKey> delivered, CancellationToken cancellationToken) =>
        inner.OpenUnblockedBatchAsync(delivered, cancellationToken);

    private sealed class Batch(IChangeBatch inner, List<double> lag) : IChangeBatch
    {
        public IReadOnlyList<SourceRowState> Rows => inner.Rows;

        public DateTimeOffset? OldestChangeAt => inner.OldestChangeAt;

        public string Position => inner.Position;

        public Task<IReadOnlySet<SourceKey>> FindParkedAsync(IReadOnlyCollection<SourceKey> keys, CancellationToken cancellationToken) =>
            inner.FindParkedAsync(keys, cancellationToken);

        public async Task CompleteAsync(IReadOnlyCollection<SourceKey> delivered, IReadOnlyCollection<ParkedRow> parked, CancellationToken cancellationToken)
        {
            await inner.CompleteAsync(delivered, parked, cancellationToken);
            if (OldestChangeAt is { } oldest)
            {
                lag.Add((DateTimeOffset.UtcNow - oldest).TotalSeconds);
            }
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
