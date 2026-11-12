using HbysFhirBridge.Mapping;
using HbysFhirBridge.Oracle;
using HbysFhirBridge.Sync;

namespace HbysFhirBridge.Worker;

/// <summary>The "Bridge" configuration section. Connection strings come from the environment, never from files.</summary>
public sealed class BridgeOptions
{
    public const string Section = "Bridge";

    /// <summary>The bridge login: SELECT/FLASHBACK on clinical tables, DELETE on the journal, owner of its own tables.</summary>
    public string ConnectionString { get; set; } = "";

    /// <summary>The HBYS schema owner. Only the install, seed and workload commands use it; the sync loop never does.</summary>
    public string? OwnerConnectionString { get; set; }

    public string SourceSchema { get; set; } = "HBYS";

    public string BridgeUser { get; set; } = "HBYS_BRIDGE";

    public string Pipeline { get; set; } = "fhir";

    public Uri FhirBaseUrl { get; set; } = new("http://localhost:18480/fhir/");

    public int BatchSize { get; set; } = 500;

    public int MaxBundleEntries { get; set; } = 250;

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>See <see cref="MappingOptions.IncludeNationalId"/>; off by default for KVKK data minimisation.</summary>
    public bool IncludeNationalId { get; set; }

    public OracleSourceOptions Source() => new()
    {
        ConnectionString = Require(ConnectionString, "Bridge:ConnectionString"),
        SourceSchema = SourceSchema,
        Pipeline = Pipeline,
    };

    public SyncOptions Sync() => new() { BatchSize = BatchSize, MaxBundleEntries = MaxBundleEntries };

    public MappingOptions Mapping() => new() { IncludeNationalId = IncludeNationalId };

    public string Owner() => Require(OwnerConnectionString, "Bridge:OwnerConnectionString");

    private static string Require(string? value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new InvalidOperationException($"{name} is not configured") : value;
}
