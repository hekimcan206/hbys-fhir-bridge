using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using HbysFhirBridge.Oracle;
using HbysFhirBridge.Synthetic;
using Microsoft.Extensions.Options;

namespace HbysFhirBridge.Worker;

/// <summary>One-shot verbs: install, seed, workload, verify.</summary>
internal static class Commands
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static async Task<int> RunAsync(string verb, string[] args, IServiceProvider services)
    {
        var options = services.GetRequiredService<IOptions<BridgeOptions>>().Value;
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        switch (verb)
        {
            case "install":
                await InstallAsync(options, cts.Token);
                return 0;

            case "seed":
                await InstallAsync(options, cts.Token);
                var dataset = SyntheticHospital.Generate(new SyntheticOptions
                {
                    Patients = Int(args, "--patients", 200),
                    DirtyRate = Double(args, "--dirty-rate", 0.02),
                    Seed = Int(args, "--seed", 20261112),
                });
                var seeded = await OracleSeeder.SeedAsync(options.Owner(), dataset, cts.Token);
                Write(new
                {
                    Seeded = seeded,
                    Patients = dataset.Patients.Count,
                    Encounters = dataset.Encounters.Count,
                    LabResults = dataset.LabResults.Count,
                    DirtyRows = dataset.Dirty.Count,
                });
                return 0;

            case "workload":
                await RunWorkloadAsync(options, Int(args, "--seconds", 30), Int(args, "--ops-per-second", 20),
                    TimeSpan.FromMilliseconds(Int(args, "--max-hold-ms", 0)), cts.Token);
                return 0;

            case "verify":
                return await VerifyAsync(services, Int(args, "--sample", 200), TimeSpan.FromSeconds(Int(args, "--wait-seconds", 120)), cts.Token);

            default:
                await Console.Error.WriteLineAsync($"Unknown command '{verb}'. Use run, install, seed, workload or verify.");
                return 2;
        }
    }

    private static async Task InstallAsync(BridgeOptions options, CancellationToken ct)
    {
        var source = await OracleSchemaInstaller.InstallSourceAsync(options.Owner(), options.BridgeUser, ct);
        var bridge = await OracleSchemaInstaller.InstallBridgeAsync(options.ConnectionString, ct);
        Write(new { SourceSchemaInstalled = source, BridgeSchemaInstalled = bridge });
    }

    private static async Task RunWorkloadAsync(BridgeOptions options, int seconds, int opsPerSecond, TimeSpan maxHold, CancellationToken ct)
    {
        await using var workload = await SourceWorkload.OpenAsync(options.Owner(), new WorkloadOptions { MaxHold = maxHold }, ct);
        var counts = new Dictionary<WorkloadOperation, int>();
        var stopwatch = Stopwatch.StartNew();
        var interval = TimeSpan.FromSeconds(1.0 / Math.Max(1, opsPerSecond));
        var steps = 0;
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(seconds) && !ct.IsCancellationRequested)
        {
            var (operation, rows) = await workload.StepAsync(ct);
            if (rows > 0)
            {
                counts[operation] = counts.GetValueOrDefault(operation) + rows;
            }

            steps++;
            var due = interval * steps;
            if (due > stopwatch.Elapsed)
            {
                await Task.Delay(due - stopwatch.Elapsed, ct);
            }
        }

        Write(new { Operations = steps, RowsChanged = counts.ToDictionary(c => c.Key.ToString(), c => c.Value) });
    }

    private static async Task<int> VerifyAsync(IServiceProvider services, int sample, TimeSpan wait, CancellationToken ct)
    {
        var status = services.GetRequiredService<BridgeStatusReader>();
        var deadline = Stopwatch.StartNew();
        BridgeStatus current;
        while ((current = await status.ReadAsync(ct)).PendingChanges > 0 && deadline.Elapsed < wait)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }

        if (current.PendingChanges > 0)
        {
            await Console.Error.WriteLineAsync($"Journal still has {current.PendingChanges} pending changes after {wait.TotalSeconds:F0} s");
            return 1;
        }

        var report = await services.GetRequiredService<ConsistencyVerifier>().VerifyAsync(sample, ct: ct);
        Write(new
        {
            report.Consistent,
            report.Counts,
            report.Parked,
            current.ParkedByReason,
            report.Compared,
            report.Mismatches,
        });
        return report.Consistent ? 0 : 1;
    }

    private static int Int(string[] args, string name, int fallback) =>
        Value(args, name) is { } v ? int.Parse(v, CultureInfo.InvariantCulture) : fallback;

    private static double Double(string[] args, string name, double fallback) =>
        Value(args, name) is { } v ? double.Parse(v, CultureInfo.InvariantCulture) : fallback;

    private static string? Value(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static void Write(object value) => Console.WriteLine(JsonSerializer.Serialize(value, Indented));
}
