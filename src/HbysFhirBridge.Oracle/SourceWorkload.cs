using HbysFhirBridge.Mapping;
using HbysFhirBridge.Synthetic;
using Oracle.ManagedDataAccess.Client;

namespace HbysFhirBridge.Oracle;

public enum WorkloadOperation
{
    NewLabResult,
    CorrectLabResult,
    FinishEncounter,
    UpdatePatientPhone,
    DeleteLabResult,
    NewEncounter,
}

public sealed record WorkloadOptions
{
    public int Seed { get; init; } = 7;

    /// <summary>
    /// How long a writer keeps its transaction open after the change and before COMMIT, drawn uniformly from
    /// [0, MaxHold]. Zero models short OLTP transactions; tens of milliseconds model a busy HBYS screen that saves
    /// several things in one transaction, which is when the timestamp strategy starts losing changes.
    /// </summary>
    public TimeSpan MaxHold { get; init; } = TimeSpan.Zero;
}

/// <summary>
/// A stream of realistic HBYS writes against the synthetic schema, run as the schema owner: new and corrected lab
/// results, finished visits, patient edits, deleted results and new visits. Used by the demo and the experiments.
/// </summary>
public sealed class SourceWorkload : IAsyncDisposable
{
    private readonly OracleConnection _connection;
    private readonly WorkloadOptions _options;
    private readonly Random _random;
    private readonly LabTest[] _tests = LabCatalog.Default.Tests.Where(t => t.UcumUnit is not null).OrderBy(t => t.LocalCode, StringComparer.Ordinal).ToArray();
    private long _maxPatient;
    private long _maxEncounter;
    private long _maxResult;

    private SourceWorkload(OracleConnection connection, WorkloadOptions options)
    {
        _connection = connection;
        _options = options;
        _random = new Random(options.Seed);
    }

    public static async Task<SourceWorkload> OpenAsync(string ownerConnectionString, WorkloadOptions? options = null, CancellationToken ct = default)
    {
        var connection = new OracleConnection(ownerConnectionString);
        await connection.OpenAsync(ct);
        var workload = new SourceWorkload(connection, options ?? new WorkloadOptions());
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT (SELECT NVL(MAX(hasta_id), 0) FROM hasta), (SELECT NVL(MAX(basvuru_id), 0) FROM basvuru WHERE basvuru_id < 10000000), " +
            "(SELECT NVL(MAX(sonuc_id), 0) FROM lab_sonuc WHERE sonuc_id < 10000000) FROM dual";
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        workload._maxPatient = reader.GetInt64(0);
        workload._maxEncounter = reader.GetInt64(1);
        workload._maxResult = reader.GetInt64(2);
        return workload;
    }

    /// <summary>Runs one operation in its own transaction; returns it and the number of rows it changed.</summary>
    public async Task<(WorkloadOperation Operation, int Rows)> StepAsync(CancellationToken ct = default)
    {
        var operation = _random.Next(100) switch
        {
            < 35 => WorkloadOperation.NewLabResult,
            < 55 => WorkloadOperation.CorrectLabResult,
            < 70 => WorkloadOperation.FinishEncounter,
            < 85 => WorkloadOperation.UpdatePatientPhone,
            < 95 => WorkloadOperation.DeleteLabResult,
            _ => WorkloadOperation.NewEncounter,
        };

        await using var tx = await _connection.BeginTransactionAsync(ct);
        var rows = await ExecuteAsync(operation, ct);
        if (_options.MaxHold > TimeSpan.Zero)
        {
            await Task.Delay(TimeSpan.FromTicks((long)(_random.NextDouble() * _options.MaxHold.Ticks)), ct);
        }

        await tx.CommitAsync(ct);
        return (operation, rows);
    }

    private async Task<int> ExecuteAsync(WorkloadOperation operation, CancellationToken ct)
    {
        await using var c = OracleSql.Command(_connection, "");
        switch (operation)
        {
            case WorkloadOperation.NewLabResult:
                var test = _tests[_random.Next(_tests.Length)];
                var row = SyntheticHospital.NewResult(0, 0, 0, test, DateTime.Now, _random);
                c.CommandText =
                    "INSERT INTO lab_sonuc (sonuc_id, basvuru_id, hasta_id, tetkik_kodu, sonuc_deger, birim, ref_alt, ref_ust, durum, sonuc_zamani) " +
                    "SELECT seq_lab_sonuc.NEXTVAL, b.basvuru_id, b.hasta_id, :kod, :deger, :birim, :alt, :ust, 'KESIN', LOCALTIMESTAMP(0) " +
                    "FROM basvuru b WHERE b.basvuru_id = :enc AND b.durum IN ('DEVAM', 'TAMAMLANDI')";
                c.Parameters.Add("kod", OracleDbType.Varchar2).Value = row.TetkikKodu;
                c.Parameters.Add("deger", OracleDbType.Decimal).Value = row.SonucDeger;
                c.Parameters.Add("birim", OracleDbType.Varchar2).Value = row.Birim;
                c.Parameters.Add("alt", OracleDbType.Decimal).Value = (object?)row.RefAlt ?? DBNull.Value;
                c.Parameters.Add("ust", OracleDbType.Decimal).Value = (object?)row.RefUst ?? DBNull.Value;
                c.Parameters.Add("enc", OracleDbType.Int64).Value = Pick(_maxEncounter);
                break;
            case WorkloadOperation.CorrectLabResult:
                c.CommandText = "UPDATE lab_sonuc SET sonuc_deger = ROUND(sonuc_deger * :f, 2), durum = 'DUZELTILDI' " +
                                "WHERE sonuc_id = :id AND sonuc_deger IS NOT NULL";
                c.Parameters.Add("f", OracleDbType.Decimal).Value = 0.9m + ((decimal)_random.NextDouble() * 0.2m);
                c.Parameters.Add("id", OracleDbType.Int64).Value = Pick(_maxResult);
                break;
            case WorkloadOperation.FinishEncounter:
                c.CommandText = "UPDATE basvuru SET durum = 'TAMAMLANDI', bitis = baslangic + NUMTODSINTERVAL(:m, 'MINUTE') " +
                                "WHERE basvuru_id = :id AND durum = 'DEVAM'";
                c.Parameters.Add("m", OracleDbType.Int32).Value = _random.Next(15, 240);
                c.Parameters.Add("id", OracleDbType.Int64).Value = Pick(_maxEncounter);
                break;
            case WorkloadOperation.UpdatePatientPhone:
                c.CommandText = "UPDATE hasta SET telefon = :tel WHERE hasta_id = :id";
                c.Parameters.Add("tel", OracleDbType.Varchar2).Value = $"05{_random.Next(30, 56)}{_random.Next(0, 10_000_000):D7}";
                c.Parameters.Add("id", OracleDbType.Int64).Value = Pick(_maxPatient);
                break;
            case WorkloadOperation.DeleteLabResult:
                c.CommandText = "DELETE FROM lab_sonuc WHERE sonuc_id = :id";
                c.Parameters.Add("id", OracleDbType.Int64).Value = Pick(_maxResult);
                break;
            case WorkloadOperation.NewEncounter:
                c.CommandText = "INSERT INTO basvuru (basvuru_id, hasta_id, basvuru_tipi, durum, baslangic, brans_kodu) " +
                                "SELECT seq_basvuru.NEXTVAL, h.hasta_id, 'A', 'DEVAM', LOCALTIMESTAMP(0), 'DAH' FROM hasta h " +
                                "WHERE h.hasta_id = :id AND h.olum_tarihi IS NULL";
                c.Parameters.Add("id", OracleDbType.Int64).Value = Pick(_maxPatient);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(operation), operation, null);
        }

        return await c.ExecuteNonQueryAsync(ct);
    }

    private long Pick(long max) => max <= 0 ? 1 : _random.NextInt64(1, max + 1);

    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}
