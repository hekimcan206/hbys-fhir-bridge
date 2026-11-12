using HbysFhirBridge.Source;
using HbysFhirBridge.Synthetic;
using Oracle.ManagedDataAccess.Client;

namespace HbysFhirBridge.Oracle;

/// <summary>
/// Loads a synthetic dataset as the schema owner with array binding (one round trip per 5,000 rows). The capture
/// triggers fire for every row, so the journal holds the whole dataset afterwards and the first bridge run is the
/// initial load: there is no separate bulk-export path to keep consistent with the incremental one.
/// </summary>
public static class OracleSeeder
{
    private const int Chunk = 5_000;

    public static async Task<bool> SeedAsync(string ownerConnectionString, SyntheticDataset dataset, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        await using var connection = new OracleConnection(ownerConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using (var count = connection.CreateCommand())
        {
            count.CommandText = "SELECT COUNT(*) FROM hasta";
            if (Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture) > 0)
            {
                return false;
            }
        }

        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var chunk in dataset.Patients.Chunk(Chunk))
        {
            await InsertPatientsAsync(connection, chunk, cancellationToken);
        }

        foreach (var chunk in dataset.Encounters.Chunk(Chunk))
        {
            await InsertEncountersAsync(connection, chunk, cancellationToken);
        }

        foreach (var chunk in dataset.LabResults.Chunk(Chunk))
        {
            await InsertLabResultsAsync(connection, chunk, cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
        return true;
    }

    internal static async Task InsertPatientsAsync(OracleConnection connection, IReadOnlyList<HastaRow> rows, CancellationToken ct)
    {
        await using var c = OracleSql.Command(connection,
            "INSERT INTO hasta (hasta_id, dosya_no, tc_kimlik_no, ad, soyad, cinsiyet, dogum_tarihi, telefon, il, ilce, olum_tarihi, aktif) " +
            "VALUES (:id, :dosya, :tc, :ad, :soyad, :cins, :dogum, :tel, :il, :ilce, :olum, :aktif)");
        c.ArrayBindCount = rows.Count;
        c.Parameters.Add("id", OracleDbType.Int64).Value = rows.Select(r => r.HastaId).ToArray();
        c.Parameters.Add("dosya", OracleDbType.Varchar2).Value = rows.Select(r => r.DosyaNo).ToArray();
        c.Parameters.Add("tc", OracleDbType.Char).Value = rows.Select(r => r.TcKimlikNo).ToArray();
        c.Parameters.Add("ad", OracleDbType.Varchar2).Value = rows.Select(r => r.Ad).ToArray();
        c.Parameters.Add("soyad", OracleDbType.Varchar2).Value = rows.Select(r => r.Soyad).ToArray();
        c.Parameters.Add("cins", OracleDbType.Char).Value = rows.Select(r => r.Cinsiyet.ToString()).ToArray();
        c.Parameters.Add("dogum", OracleDbType.Date).Value = rows.Select(r => Date(r.DogumTarihi)).ToArray();
        c.Parameters.Add("tel", OracleDbType.Varchar2).Value = rows.Select(r => r.Telefon).ToArray();
        c.Parameters.Add("il", OracleDbType.Varchar2).Value = rows.Select(r => r.Il).ToArray();
        c.Parameters.Add("ilce", OracleDbType.Varchar2).Value = rows.Select(r => r.Ilce).ToArray();
        c.Parameters.Add("olum", OracleDbType.Date).Value = rows.Select(r => Date(r.OlumTarihi)).ToArray();
        c.Parameters.Add("aktif", OracleDbType.Char).Value = rows.Select(r => r.Aktif ? "E" : "H").ToArray();
        await c.ExecuteNonQueryAsync(ct);
    }

    internal static async Task InsertEncountersAsync(OracleConnection connection, IReadOnlyList<BasvuruRow> rows, CancellationToken ct)
    {
        await using var c = OracleSql.Command(connection,
            "INSERT INTO basvuru (basvuru_id, hasta_id, basvuru_tipi, durum, baslangic, bitis, brans_kodu) " +
            "VALUES (:id, :hasta, :tip, :durum, :bas, :bit, :brans)");
        c.ArrayBindCount = rows.Count;
        c.Parameters.Add("id", OracleDbType.Int64).Value = rows.Select(r => r.BasvuruId).ToArray();
        c.Parameters.Add("hasta", OracleDbType.Int64).Value = rows.Select(r => r.HastaId).ToArray();
        c.Parameters.Add("tip", OracleDbType.Char).Value = rows.Select(r => r.BasvuruTipi.ToString()).ToArray();
        c.Parameters.Add("durum", OracleDbType.Varchar2).Value = rows.Select(r => r.Durum).ToArray();
        c.Parameters.Add("bas", OracleDbType.TimeStamp).Value = rows.Select(r => (object)r.Baslangic).ToArray();
        c.Parameters.Add("bit", OracleDbType.TimeStamp).Value = rows.Select(r => r.Bitis is { } b ? (object)b : DBNull.Value).ToArray();
        c.Parameters.Add("brans", OracleDbType.Varchar2).Value = rows.Select(r => r.BransKodu).ToArray();
        await c.ExecuteNonQueryAsync(ct);
    }

    internal static async Task InsertLabResultsAsync(OracleConnection connection, IReadOnlyList<LabSonucRow> rows, CancellationToken ct)
    {
        await using var c = OracleSql.Command(connection,
            "INSERT INTO lab_sonuc (sonuc_id, basvuru_id, hasta_id, tetkik_kodu, sonuc_deger, sonuc_metin, birim, ref_alt, ref_ust, durum, sonuc_zamani) " +
            "VALUES (:id, :bas, :hasta, :kod, :deger, :metin, :birim, :alt, :ust, :durum, :zaman)");
        c.ArrayBindCount = rows.Count;
        c.Parameters.Add("id", OracleDbType.Int64).Value = rows.Select(r => r.SonucId).ToArray();
        c.Parameters.Add("bas", OracleDbType.Int64).Value = rows.Select(r => r.BasvuruId).ToArray();
        c.Parameters.Add("hasta", OracleDbType.Int64).Value = rows.Select(r => r.HastaId).ToArray();
        c.Parameters.Add("kod", OracleDbType.Varchar2).Value = rows.Select(r => r.TetkikKodu).ToArray();
        c.Parameters.Add("deger", OracleDbType.Decimal).Value = rows.Select(r => Number(r.SonucDeger)).ToArray();
        c.Parameters.Add("metin", OracleDbType.Varchar2).Value = rows.Select(r => r.SonucMetin).ToArray();
        c.Parameters.Add("birim", OracleDbType.Varchar2).Value = rows.Select(r => r.Birim).ToArray();
        c.Parameters.Add("alt", OracleDbType.Decimal).Value = rows.Select(r => Number(r.RefAlt)).ToArray();
        c.Parameters.Add("ust", OracleDbType.Decimal).Value = rows.Select(r => Number(r.RefUst)).ToArray();
        c.Parameters.Add("durum", OracleDbType.Varchar2).Value = rows.Select(r => r.Durum).ToArray();
        c.Parameters.Add("zaman", OracleDbType.TimeStamp).Value = rows.Select(r => (object)r.SonucZamani).ToArray();
        await c.ExecuteNonQueryAsync(ct);
    }

    private static object Date(DateOnly? value) => value is { } d ? d.ToDateTime(TimeOnly.MinValue) : DBNull.Value;

    private static object Number(decimal? value) => value is { } d ? d : DBNull.Value;
}
