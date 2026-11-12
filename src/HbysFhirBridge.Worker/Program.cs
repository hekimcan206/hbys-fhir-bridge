using HbysFhirBridge.Fhir;
using HbysFhirBridge.Mapping;
using HbysFhirBridge.Oracle;
using HbysFhirBridge.Sync;
using HbysFhirBridge.Validation;
using HbysFhirBridge.Worker;
using Microsoft.Extensions.Options;

// One image, several roles. With no verb (or "run") it is the long-running bridge; the other verbs are one-shot
// operator/demo commands that exit with 0 on success.
var verb = args.Length > 0 && !args[0].StartsWith('-') ? args[0] : "run";
var builder = WebApplication.CreateBuilder(args);
builder.AddObservability();

builder.Services.AddOptions<BridgeOptions>().Bind(builder.Configuration.GetSection(BridgeOptions.Section));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<BridgeOptions>>().Value.Source());
builder.Services.AddSingleton(sp => new FhirMapper(LabCatalog.Default, sp.GetRequiredService<IOptions<BridgeOptions>>().Value.Mapping()));
builder.Services.AddSingleton<ResourceValidator>();
builder.Services.AddSingleton<IResourceValidator>(sp => sp.GetRequiredService<ResourceValidator>());
builder.Services.AddSingleton<IChangeSource, OracleJournalSource>();
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<BridgeOptions>>().Value.Sync());
builder.Services.AddSingleton<SyncEngine>();
builder.Services.AddSingleton<SyncState>();
builder.Services.AddSingleton<BridgeStatusReader>();
builder.Services.AddSingleton<SourceSnapshotReader>();
builder.Services.AddSingleton<ConsistencyVerifier>();

builder.Services.AddHttpClient<IFhirSink, FhirHttpSink>(ConfigureFhirClient)
    .AddStandardResilienceHandler(resilience =>
    {
        // A 250-entry transaction can take seconds on a busy server; the defaults (10 s per attempt) are too tight.
        // Retrying the POST is safe because every entry is an idempotent PUT or DELETE (docs/adr/0003).
        resilience.AttemptTimeout.Timeout = TimeSpan.FromSeconds(60);
        resilience.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(120);
        resilience.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(3);
    });
builder.Services.AddHttpClient<FhirReadClient>(ConfigureFhirClient);

builder.Services.AddHealthChecks().AddCheck<SyncHealthCheck>("sync", tags: ["ready"]);
if (verb == "run")
{
    builder.Services.AddHostedService<SyncWorker>();
}

var app = builder.Build();

if (verb != "run")
{
    Environment.ExitCode = await Commands.RunAsync(verb, args.Skip(1).ToArray(), app.Services);
    return;
}

app.MapHealthChecks("/health/live", new() { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new() { Predicate = c => c.Tags.Contains("ready") });
app.MapGet("/status", async (BridgeStatusReader reader, SyncState state, CancellationToken ct) =>
{
    var status = await reader.ReadAsync(ct);
    return Results.Ok(new
    {
        status.PendingChanges,
        status.OldestPendingAt,
        status.Position,
        status.Batches,
        status.Parked,
        status.ParkedByReason,
        state.LastSuccess,
        state.LastError,
        Totals = new { state.Totals.Batches, state.Totals.Rows, state.Totals.Delivered, state.Totals.Parked, state.Totals.BundlesSent },
    });
});

await app.RunAsync();

static void ConfigureFhirClient(IServiceProvider services, HttpClient http)
{
    var baseUrl = services.GetRequiredService<IOptions<BridgeOptions>>().Value.FhirBaseUrl.ToString();
    http.BaseAddress = new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/");
    http.Timeout = Timeout.InfiniteTimeSpan; // the resilience pipeline owns timeouts
}

/// <summary>Entry point; public so WebApplicationFactory can boot the app in integration tests.</summary>
public partial class Program;
