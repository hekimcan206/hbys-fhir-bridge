using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Oracle.ManagedDataAccess.Client;

namespace HbysFhirBridge.Oracle;

internal static partial class OracleSql
{
    /// <summary>Oracle caps an IN list at 1000 expressions; stay well below it.</summary>
    public const int InListChunk = 500;

    /// <summary>
    /// Validates a schema or user name before it is spliced into SQL. Identifiers cannot be bound as parameters, so
    /// this allow-list is what keeps configuration from becoming SQL injection.
    /// </summary>
    public static string Identifier(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!IdentifierPattern().IsMatch(name))
        {
            throw new ArgumentException($"'{name}' is not a plain Oracle identifier", nameof(name));
        }

        return name.ToUpperInvariant();
    }

    /// <summary>Appends ":p0, :p1, ..." to <paramref name="sql"/> and binds <paramref name="values"/> to them.</summary>
    public static void AppendInList(StringBuilder sql, OracleCommand command, IReadOnlyList<long> values, string prefix = "p")
    {
        for (var i = 0; i < values.Count; i++)
        {
            var name = prefix + i.ToString(CultureInfo.InvariantCulture);
            sql.Append(i == 0 ? ":" : ", :").Append(name);
            command.Parameters.Add(name, OracleDbType.Int64).Value = values[i];
        }
    }

    public static OracleCommand Command(OracleConnection connection, string sql)
    {
        var command = connection.CreateCommand();
        command.BindByName = true;
        command.CommandText = sql;
        return command;
    }

    /// <summary>Splits a script on lines that contain only "/" (SQL*Plus convention), dropping empty chunks.</summary>
    public static IEnumerable<string> SplitScript(string script) =>
        SlashLine().Split(script.Replace("\r\n", "\n", StringComparison.Ordinal))
            .Select(s => s.Trim())
            .Where(s => s.Length > 0 && !s.Split('\n').All(l => l.TrimStart().StartsWith("--", StringComparison.Ordinal) || l.Trim().Length == 0));

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]{0,29}$")]
    private static partial Regex IdentifierPattern();

    [GeneratedRegex(@"^\s*/\s*$", RegexOptions.Multiline)]
    private static partial Regex SlashLine();
}
