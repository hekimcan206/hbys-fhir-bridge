using System.Text;
using Oracle.ManagedDataAccess.Client;

namespace HbysFhirBridge.Oracle;

/// <summary>
/// Installs the synthetic HBYS schema (as its owner) and the bridge's own tables (as the bridge login). Both steps
/// are skipped when the objects already exist, so the demo seed can run more than once.
/// </summary>
public static class OracleSchemaInstaller
{
    public static async Task<bool> InstallSourceAsync(string ownerConnectionString, string bridgeUser, CancellationToken cancellationToken = default)
    {
        var grantee = OracleSql.Identifier(bridgeUser);
        await using var connection = new OracleConnection(ownerConnectionString);
        await connection.OpenAsync(cancellationToken);
        if (await TableExistsAsync(connection, "DEGISIKLIK_GUNLUGU", cancellationToken))
        {
            return false;
        }

        await RunScriptAsync(connection, "01-hbys-schema.sql", s => s, cancellationToken);
        await RunScriptAsync(connection, "02-hbys-grants.sql", s => s.Replace("{bridge}", grantee, StringComparison.Ordinal), cancellationToken);
        await AssertTriggersValidAsync(connection, cancellationToken);
        return true;
    }

    public static async Task<bool> InstallBridgeAsync(string bridgeConnectionString, CancellationToken cancellationToken = default)
    {
        await using var connection = new OracleConnection(bridgeConnectionString);
        await connection.OpenAsync(cancellationToken);
        if (await TableExistsAsync(connection, "BRIDGE_CHECKPOINT", cancellationToken))
        {
            return false;
        }

        await RunScriptAsync(connection, "03-bridge-schema.sql", s => s, cancellationToken);
        return true;
    }

    private static async Task RunScriptAsync(OracleConnection connection, string name, Func<string, string> transform, CancellationToken ct)
    {
        foreach (var statement in OracleSql.SplitScript(ReadScript(name)))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = transform(statement);
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task<bool> TableExistsAsync(OracleConnection connection, string table, CancellationToken ct)
    {
        await using var command = OracleSql.Command(connection, "SELECT COUNT(*) FROM user_tables WHERE table_name = :t");
        command.Parameters.Add("t", OracleDbType.Varchar2).Value = table;
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture) > 0;
    }

    private static async Task AssertTriggersValidAsync(OracleConnection connection, CancellationToken ct)
    {
        await using var command = OracleSql.Command(connection,
            "SELECT LISTAGG(object_name, ', ') WITHIN GROUP (ORDER BY object_name) FROM user_objects WHERE object_type = 'TRIGGER' AND status <> 'VALID'");
        if (await command.ExecuteScalarAsync(ct) is string invalid && invalid.Length > 0)
        {
            throw new InvalidOperationException("Change capture triggers did not compile: " + invalid);
        }
    }

    internal static string ReadScript(string name)
    {
        using var stream = typeof(OracleSchemaInstaller).Assembly.GetManifestResourceStream("HbysFhirBridge.Oracle.Sql." + name)
            ?? throw new InvalidOperationException($"Embedded script {name} is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
