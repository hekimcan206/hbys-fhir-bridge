using HbysFhirBridge.Fhir;
using HbysFhirBridge.Oracle;
using HbysFhirBridge.Sync;
using HbysFhirBridge.Validation;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Oracle.ManagedDataAccess.Client;

namespace HbysFhirBridge.Worker;

/// <summary>What the loop last did, for /status and the readiness check.</summary>
public sealed class SyncState
{
    private readonly Lock _gate = new();

    public bool Warm { get; private set; }

    public DateTimeOffset? LastSuccess { get; private set; }

    public string? LastError { get; private set; }

    public SyncReport Totals { get; private set; } = SyncReport.Empty;

    internal void MarkWarm() => Warm = true;

    internal void Succeeded(SyncReport report, DateTimeOffset at)
    {
        lock (_gate)
        {
            Totals = new SyncReport(
                Totals.Batches + report.Batches, Totals.Rows + report.Rows, Totals.Delivered + report.Delivered,
                Totals.Parked + report.Parked, Totals.BundlesSent + report.BundlesSent, Totals.Elapsed + report.Elapsed);
            LastSuccess = at;
            LastError = null;
        }
    }

    internal void Failed(string error) => LastError = error;
}

/// <summary>
/// The sync loop: drain the journal, sleep, repeat. Every failure mode leaves the journal untouched (the batch rolls
/// back), so the loop only has to log, back off and try again.
/// </summary>
public sealed partial class SyncWorker(
    SyncEngine engine,
    ResourceValidator validator,
    SyncState state,
    IOptions<BridgeOptions> options,
    TimeProvider time,
    ILogger<SyncWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The first validation of each resource type compiles its profile (seconds); do it before declaring ready.
        await Task.Run(validator.WarmUp, stoppingToken);
        state.MarkWarm();

        var backoff = options.Value.PollInterval;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var report = await engine.DrainAsync(stoppingToken);
                state.Succeeded(report, time.GetUtcNow());
                if (report.Batches > 0)
                {
                    LogDrained(report.Batches, report.Rows, report.Delivered, report.Parked, report.BundlesSent, report.Elapsed.TotalMilliseconds);
                }

                backoff = options.Value.PollInterval;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is PipelineBusyException or FhirServerUnavailableException or OracleException or HttpRequestException or TimeoutException
                                           or Polly.Timeout.TimeoutRejectedException)
            {
                state.Failed(ex.Message);
                LogRetrying(ex.GetType().Name, ex.Message, backoff.TotalSeconds);
                backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, TimeSpan.FromSeconds(30).Ticks));
            }

            await Task.Delay(backoff, time, stoppingToken).ContinueWith(_ => { }, TaskScheduler.Default);
        }
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Drained {Batches} batches: {Rows} rows, {Delivered} delivered, {Parked} parked, {Bundles} bundles in {ElapsedMs:F0} ms")]
    private partial void LogDrained(int batches, int rows, int delivered, int parked, int bundles, double elapsedMs);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sync cycle failed ({Error}: {Message}); nothing acknowledged, retrying in {Seconds} s")]
    private partial void LogRetrying(string error, string message, double seconds);
}

/// <summary>Ready when the validator is warm and a cycle succeeded within the last three poll intervals (or 30 s).</summary>
public sealed class SyncHealthCheck(SyncState state, IOptions<BridgeOptions> options, TimeProvider time) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (!state.Warm)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("validator warming up"));
        }

        var window = TimeSpan.FromTicks(Math.Max(options.Value.PollInterval.Ticks * 3, TimeSpan.FromSeconds(30).Ticks));
        return Task.FromResult(state.LastSuccess is { } last && time.GetUtcNow() - last <= window
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy(state.LastError ?? "no successful sync cycle yet"));
    }
}
