using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using HbysFhirBridge.Fhir;
using HbysFhirBridge.Mapping;
using HbysFhirBridge.Oracle;
using HbysFhirBridge.Sync;
using HbysFhirBridge.Validation;
using Oracle.ManagedDataAccess.Client;
using Testcontainers.Oracle;

[assembly: AssemblyFixture(typeof(HbysFhirBridge.IntegrationTests.BridgeFixture))]

namespace HbysFhirBridge.IntegrationTests;

/// <summary>
/// One Oracle Database 23ai Free and one Blaze FHIR server per test run. The users come from the same init script
/// docker compose mounts; schemas are installed by the real installer. Tests share both servers and leave them empty
/// (see <see cref="ResetAsync"/>), deleting through the bridge itself so that cleanup also exercises DELETE.
/// </summary>
public sealed class BridgeFixture : IAsyncLifetime
{
    // Test-only credentials for throwaway containers.
    private const string OwnerPassword = "HbysTest1";
    private const string BridgePassword = "BridgeTest1";

    private readonly OracleContainer _oracle = new OracleBuilder("gvenzl/oracle-free:23-slim-faststart")
        .WithEnvironment("HBYS_PASSWORD", OwnerPassword)
        .WithEnvironment("BRIDGE_PASSWORD", BridgePassword)
        .WithResourceMapping(
            File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "oracle-init", "01-users.sh")),
            "/container-entrypoint-initdb.d/01-users.sh",
            fileMode: UnixFileModes.UserRead | UnixFileModes.UserWrite | UnixFileModes.UserExecute | UnixFileModes.GroupRead | UnixFileModes.OtherRead)
        .WithLabel("project", "hbys-fhir-bridge-tests")
        .Build();

    private readonly IContainer _blaze = new ContainerBuilder("samply/blaze:1.12.0")
        .WithPortBinding(8080, true)
        .WithEnvironment("JAVA_TOOL_OPTIONS", "-Xmx512m")
        .WithEnvironment("DB_BLOCK_CACHE_SIZE", "64")
        .WithEnvironment("DB_RESOURCE_CACHE_SIZE", "10000")
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(8080).ForPath("/health")))
        .WithLabel("project", "hbys-fhir-bridge-tests")
        .Build();

    public string OwnerConnectionString => WithCredentials("hbys", OwnerPassword);

    public string BridgeConnectionString => WithCredentials("hbys_bridge", BridgePassword);

    public ResourceValidator Validator { get; } = new();

    public Uri FhirBase => new($"http://{_blaze.Hostname}:{_blaze.GetMappedPublicPort(8080)}/fhir/");

    public OracleSourceOptions SourceOptions(string pipeline = "fhir") => new()
    {
        ConnectionString = BridgeConnectionString,
        Pipeline = pipeline,
    };

    public HttpClient FhirHttp() => new() { BaseAddress = FhirBase, Timeout = TimeSpan.FromMinutes(2) };

    public FhirReadClient Reader() => new(FhirHttp());

    public SyncEngine Engine(IChangeSource? source = null, IFhirSink? sink = null, SyncOptions? options = null) =>
        new(source ?? new OracleJournalSource(SourceOptions()), sink ?? new FhirHttpSink(FhirHttp()), new FhirMapper(), Validator,
            options ?? new SyncOptions());

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(_oracle.StartAsync(), _blaze.StartAsync());
        await OracleSchemaInstaller.InstallSourceAsync(OwnerConnectionString, "hbys_bridge");
        await OracleSchemaInstaller.InstallBridgeAsync(BridgeConnectionString);
        // Compile the three profiles once, outside any test's timing.
        Validator.WarmUp();
    }

    /// <summary>Deletes every source row, lets the bridge propagate the deletes, and clears bridge state.</summary>
    public async Task ResetAsync()
    {
        await ExecuteAsOwnerAsync("DELETE FROM lab_sonuc", "DELETE FROM basvuru", "DELETE FROM hasta");
        await Engine().DrainAsync();
        await using var connection = new OracleConnection(BridgeConnectionString);
        await connection.OpenAsync();
        foreach (var sql in new[] { "DELETE FROM bridge_parked", "DELETE FROM bridge_checkpoint", "DELETE FROM hbys.degisiklik_gunlugu" })
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }
    }

    public async Task ExecuteAsOwnerAsync(params string[] statements)
    {
        await using var connection = new OracleConnection(OwnerConnectionString);
        await connection.OpenAsync();
        foreach (var sql in statements)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _blaze.DisposeAsync();
        await _oracle.DisposeAsync();
    }

    private string WithCredentials(string user, string password) =>
        new OracleConnectionStringBuilder(_oracle.GetConnectionString()) { UserID = user, Password = password }.ConnectionString;
}
