using System.Data.Common;
using System.Text;
using HbysFhirBridge.Source;
using Oracle.ManagedDataAccess.Client;

namespace HbysFhirBridge.Oracle;

/// <summary>
/// Reads the current state of changed rows, optionally AS OF an SCN. Reading every table at the journal's SCN is what
/// makes a batch a consistent snapshot: a lab result and the visit it belongs to come from the same instant.
/// </summary>
internal sealed class SourceRowReader(string schema)
{
    private const string HastaColumns =
        "hasta_id, dosya_no, tc_kimlik_no, ad, soyad, cinsiyet, dogum_tarihi, telefon, il, ilce, olum_tarihi, aktif, guncelleme_zamani";

    private const string BasvuruColumns =
        "basvuru_id, hasta_id, basvuru_tipi, durum, baslangic, bitis, brans_kodu, guncelleme_zamani";

    private const string LabColumns =
        "sonuc_id, basvuru_id, hasta_id, tetkik_kodu, sonuc_deger, sonuc_metin, birim, ref_alt, ref_ust, durum, sonuc_zamani, guncelleme_zamani";

    private readonly string _schema = OracleSql.Identifier(schema);

    /// <summary>States for <paramref name="keys"/>; a key with no row comes back as deleted.</summary>
    public async Task<List<SourceRowState>> ReadAsync(
        OracleConnection connection, IReadOnlyCollection<SourceKey> keys, long? asOfScn, CancellationToken ct)
    {
        var found = new Dictionary<SourceKey, object>();
        foreach (var group in keys.GroupBy(k => k.Table))
        {
            var ids = group.Select(k => k.Id).Distinct().ToList();
            foreach (var chunk in ids.Chunk(OracleSql.InListChunk))
            {
                var (columns, table, idColumn) = Describe(group.Key);
                var sql = new StringBuilder()
                    .Append("SELECT ").Append(columns).Append(" FROM ").Append(_schema).Append('.').Append(table);
                await using var command = OracleSql.Command(connection, "");
                if (asOfScn is { } scn)
                {
                    sql.Append(" AS OF SCN :scn");
                    command.Parameters.Add("scn", OracleDbType.Int64).Value = scn;
                }

                sql.Append(" WHERE ").Append(idColumn).Append(" IN (");
                OracleSql.AppendInList(sql, command, chunk);
                sql.Append(')');
                command.CommandText = sql.ToString();

                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var (key, row) = ReadRow(group.Key, reader);
                    found[key] = row;
                }
            }
        }

        return keys.Distinct().Select(k => new SourceRowState(k, found.GetValueOrDefault(k))).ToList();
    }

    /// <summary>Rows of one table changed after <paramref name="after"/>, by the "last updated" stamp (naive strategy).</summary>
    public async Task<List<(SourceRowState State, DateTime Stamp)>> ReadStampedAfterAsync(
        OracleConnection connection, SourceTable table, DateTime after, CancellationToken ct)
    {
        var (columns, name, _) = Describe(table);
        await using var command = OracleSql.Command(connection,
            $"SELECT {columns} FROM {_schema}.{name} WHERE guncelleme_zamani > :after ORDER BY guncelleme_zamani");
        command.Parameters.Add("after", OracleDbType.TimeStamp).Value = after;
        var result = new List<(SourceRowState, DateTime)>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        var stampOrdinal = reader.GetOrdinal("GUNCELLEME_ZAMANI");
        while (await reader.ReadAsync(ct))
        {
            var (key, row) = ReadRow(table, reader);
            result.Add((new SourceRowState(key, row), reader.GetDateTime(stampOrdinal)));
        }

        return result;
    }

    /// <summary>Every row of a table (verification and tests only).</summary>
    public async Task<List<SourceRowState>> ReadAllAsync(OracleConnection connection, SourceTable table, CancellationToken ct)
    {
        var (columns, name, _) = Describe(table);
        await using var command = OracleSql.Command(connection, $"SELECT {columns} FROM {_schema}.{name}");
        var result = new List<SourceRowState>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var (key, row) = ReadRow(table, reader);
            result.Add(new SourceRowState(key, row));
        }

        return result;
    }

    private static (string Columns, string Table, string IdColumn) Describe(SourceTable table) => table switch
    {
        SourceTable.Hasta => (HastaColumns, "hasta", "hasta_id"),
        SourceTable.Basvuru => (BasvuruColumns, "basvuru", "basvuru_id"),
        SourceTable.LabSonuc => (LabColumns, "lab_sonuc", "sonuc_id"),
        _ => throw new ArgumentOutOfRangeException(nameof(table), table, null),
    };

    internal static (SourceKey Key, object Row) ReadRow(SourceTable table, DbDataReader r)
    {
        switch (table)
        {
            case SourceTable.Hasta:
                var hasta = new HastaRow(
                    r.GetInt64(0), r.GetString(1), Text(r, 2), r.GetString(3), r.GetString(4), r.GetString(5)[0],
                    Date(r, 6), Text(r, 7), Text(r, 8), Text(r, 9), Date(r, 10), r.GetString(11) == "E");
                return (new SourceKey(SourceTable.Hasta, hasta.HastaId), hasta);
            case SourceTable.Basvuru:
                var basvuru = new BasvuruRow(
                    r.GetInt64(0), r.GetInt64(1), r.GetString(2)[0], r.GetString(3), r.GetDateTime(4),
                    r.IsDBNull(5) ? null : r.GetDateTime(5), Text(r, 6));
                return (new SourceKey(SourceTable.Basvuru, basvuru.BasvuruId), basvuru);
            case SourceTable.LabSonuc:
                var lab = new LabSonucRow(
                    r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetString(3), Number(r, 4), Text(r, 5), Text(r, 6),
                    Number(r, 7), Number(r, 8), r.GetString(9), r.GetDateTime(10));
                return (new SourceKey(SourceTable.LabSonuc, lab.SonucId), lab);
            default:
                throw new ArgumentOutOfRangeException(nameof(table), table, null);
        }
    }

    private static string? Text(DbDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static DateOnly? Date(DbDataReader r, int i) => r.IsDBNull(i) ? null : DateOnly.FromDateTime(r.GetDateTime(i));

    private static decimal? Number(DbDataReader r, int i) => r.IsDBNull(i) ? null : r.GetDecimal(i);
}
