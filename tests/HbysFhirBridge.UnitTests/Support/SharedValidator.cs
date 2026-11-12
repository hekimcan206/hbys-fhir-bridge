using HbysFhirBridge.Source;
using HbysFhirBridge.Validation;

[assembly: AssemblyFixture(typeof(HbysFhirBridge.UnitTests.Support.SharedValidator))]

namespace HbysFhirBridge.UnitTests.Support;

/// <summary>One Firely validator per test run: the first validation per type compiles profiles and takes seconds.</summary>
public sealed class SharedValidator
{
    public ResourceValidator Validator { get; } = new();
}

/// <summary>Rows used across tests. Times are local HBYS times (UTC+3).</summary>
internal static class Rows
{
    public static readonly DateTime Morning = new(2026, 3, 2, 9, 30, 0, DateTimeKind.Unspecified);

    public static HastaRow Patient(long id = 1, DateOnly? death = null) =>
        new(id, $"D{id:D8}", "10000000146", "Ayşe", "Yılmaz", 'K', new DateOnly(1984, 5, 17), "05321234567", "Ankara", "Çankaya", death, death is null);

    public static BasvuruRow Encounter(long id = 10, long patient = 1, string status = "TAMAMLANDI", DateTime? end = null) =>
        new(id, patient, 'A', status, Morning, status == "TAMAMLANDI" ? end ?? Morning.AddMinutes(25) : end, "DAH");

    public static LabSonucRow Lab(long id = 100, long encounter = 10, long patient = 1, string code = "BIY-GLU", decimal? value = 112m, string? unit = "mg/dl") =>
        new(id, encounter, patient, code, value, null, unit, 70m, 100m, "KESIN", Morning.AddHours(1));

    public static SourceKey Key(SourceTable table, long id) => new(table, id);
}
