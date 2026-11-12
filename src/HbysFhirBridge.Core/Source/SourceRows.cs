namespace HbysFhirBridge.Source;

/// <summary>The HBYS tables the bridge exports. Each maps to exactly one FHIR resource type.</summary>
public enum SourceTable
{
    /// <summary>HASTA, exported as Patient.</summary>
    Hasta,

    /// <summary>BASVURU, exported as Encounter.</summary>
    Basvuru,

    /// <summary>LAB_SONUC, exported as Observation.</summary>
    LabSonuc,
}

/// <summary>Identifies one source row: the unit the change journal records and the bridge synchronises.</summary>
public readonly record struct SourceKey(SourceTable Table, long Id)
{
    public override string ToString() => $"{SourceTables.ToDbName(Table)}:{Id}";
}

public static class SourceTables
{
    public static string ToDbName(SourceTable table) => table switch
    {
        SourceTable.Hasta => "HASTA",
        SourceTable.Basvuru => "BASVURU",
        SourceTable.LabSonuc => "LAB_SONUC",
        _ => throw new ArgumentOutOfRangeException(nameof(table), table, null),
    };

    public static SourceTable FromDbName(string name) => name switch
    {
        "HASTA" => SourceTable.Hasta,
        "BASVURU" => SourceTable.Basvuru,
        "LAB_SONUC" => SourceTable.LabSonuc,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown journal table"),
    };

    public static string ToResourceType(SourceTable table) => table switch
    {
        SourceTable.Hasta => "Patient",
        SourceTable.Basvuru => "Encounter",
        SourceTable.LabSonuc => "Observation",
        _ => throw new ArgumentOutOfRangeException(nameof(table), table, null),
    };

    public static SourceTable FromResourceType(string resourceType) => resourceType switch
    {
        "Patient" => SourceTable.Hasta,
        "Encounter" => SourceTable.Basvuru,
        "Observation" => SourceTable.LabSonuc,
        _ => throw new ArgumentOutOfRangeException(nameof(resourceType), resourceType, null),
    };

    /// <summary>
    /// Parents before children. Bundles are written in this order so that a reader of the bundle (and the bisecting
    /// retry path, which sends halves in order) always meets a referenced resource before the reference.
    /// </summary>
    public static int DependencyRank(SourceTable table) => table switch
    {
        SourceTable.Hasta => 0,
        SourceTable.Basvuru => 1,
        SourceTable.LabSonuc => 2,
        _ => throw new ArgumentOutOfRangeException(nameof(table), table, null),
    };
}

/// <summary>A row of HASTA (patient). Column names are explained in the README ("Source schema").</summary>
public sealed record HastaRow(
    long HastaId,
    string DosyaNo,
    string? TcKimlikNo,
    string Ad,
    string Soyad,
    char Cinsiyet,
    DateOnly? DogumTarihi,
    string? Telefon,
    string? Il,
    string? Ilce,
    DateOnly? OlumTarihi,
    bool Aktif);

/// <summary>A row of BASVURU (visit or admission).</summary>
public sealed record BasvuruRow(
    long BasvuruId,
    long HastaId,
    char BasvuruTipi,
    string Durum,
    DateTime Baslangic,
    DateTime? Bitis,
    string? BransKodu);

/// <summary>A row of LAB_SONUC (laboratory result).</summary>
public sealed record LabSonucRow(
    long SonucId,
    long BasvuruId,
    long HastaId,
    string TetkikKodu,
    decimal? SonucDeger,
    string? SonucMetin,
    string? Birim,
    decimal? RefAlt,
    decimal? RefUst,
    string Durum,
    DateTime SonucZamani);

/// <summary>The current state of a changed source row, or <c>null</c> when the row no longer exists (deleted).</summary>
public sealed record SourceRowState(SourceKey Key, object? Row)
{
    public bool IsDeleted => Row is null;
}
