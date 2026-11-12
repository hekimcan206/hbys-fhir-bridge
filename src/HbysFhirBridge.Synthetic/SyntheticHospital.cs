using Bogus;
using HbysFhirBridge.Mapping;
using HbysFhirBridge.Source;

namespace HbysFhirBridge.Synthetic;

/// <summary>Kinds of deliberately broken rows, each of which the bridge must park rather than deliver.</summary>
public enum DirtyKind
{
    None,

    /// <summary>OLUM_TARIHI before DOGUM_TARIHI: fails profile invariant hbys-pat-2.</summary>
    DeathBeforeBirth,

    /// <summary>DURUM = TAMAMLANDI with no BITIS: fails profile invariant hbys-enc-1.</summary>
    FinishedWithoutEnd,

    /// <summary>A local test code with no LOINC mapping.</summary>
    UnmappedTestCode,

    /// <summary>A unit spelling that is not known to mean the test's UCUM unit (never silently converted).</summary>
    UnknownUnit,
}

public sealed record SyntheticOptions
{
    public int Seed { get; init; } = 20261112;

    public int Patients { get; init; } = 100;

    public int MaxEncountersPerPatient { get; init; } = 4;

    public int MaxLabResultsPerEncounter { get; init; } = 6;

    /// <summary>Share of rows (per table) that are made dirty on purpose. 0 gives a fully valid dataset.</summary>
    public double DirtyRate { get; init; } = 0.01;

    /// <summary>Export clock start. Fixed so that the same seed always yields byte-identical rows.</summary>
    public DateTime Start { get; init; } = new(2026, 1, 5, 8, 0, 0, DateTimeKind.Unspecified);
}

public sealed record SyntheticDataset(
    IReadOnlyList<HastaRow> Patients,
    IReadOnlyList<BasvuruRow> Encounters,
    IReadOnlyList<LabSonucRow> LabResults,
    IReadOnlyDictionary<SourceKey, DirtyKind> Dirty)
{
    public int RowCount => Patients.Count + Encounters.Count + LabResults.Count;

    public IEnumerable<(SourceKey Key, object Row)> AllRows() =>
        Patients.Select(p => (new SourceKey(SourceTable.Hasta, p.HastaId), (object)p))
            .Concat(Encounters.Select(e => (new SourceKey(SourceTable.Basvuru, e.BasvuruId), (object)e)))
            .Concat(LabResults.Select(l => (new SourceKey(SourceTable.LabSonuc, l.SonucId), (object)l)));
}

/// <summary>
/// Generates a synthetic hospital: Turkish names (Bogus "tr" locale), checksum-valid but random national ids,
/// visits and LOINC-mappable laboratory results with realistic value ranges. Deterministic per seed.
/// </summary>
public static class SyntheticHospital
{
    private static readonly (string Il, string Ilce)[] Places =
    [
        ("Ankara", "Çankaya"), ("Ankara", "Keçiören"), ("İstanbul", "Kadıköy"), ("İstanbul", "Üsküdar"),
        ("İzmir", "Bornova"), ("Bursa", "Nilüfer"), ("Antalya", "Muratpaşa"), ("Konya", "Selçuklu"),
        ("Eskişehir", "Tepebaşı"), ("Trabzon", "Ortahisar"), ("Gaziantep", "Şahinbey"), ("Kayseri", "Melikgazi"),
    ];

    private static readonly string[] Departments = ["DAH", "KAR", "END", "NEF", "GAS", "ENF", "ACL", "GCR"];

    // Typical value and spread per test; reference ranges are the adult ranges labs commonly print.
    private static readonly Dictionary<string, (double Mean, double Sd, decimal? Low, decimal? High, int Decimals)> Values = new()
    {
        ["BIY-GLU"] = (100, 25, 70, 100, 0),
        ["BIY-KRE"] = (0.95, 0.3, 0.6m, 1.2m, 2),
        ["BIY-ALT"] = (28, 14, 0, 41, 0),
        ["BIY-NA"] = (139, 3.5, 136, 145, 0),
        ["BIY-K"] = (4.3, 0.5, 3.5m, 5.1m, 1),
        ["BIY-CRP"] = (6, 9, 0, 5, 1),
        ["HOR-TSH"] = (2.1, 1.3, 0.27m, 4.2m, 2),
        ["HEM-HGB"] = (13.6, 1.8, 12, 17.5m, 1),
        ["HEM-WBC"] = (7.4, 2.4, 4, 10.5m, 1),
        ["HEM-PLT"] = (260, 70, 150, 400, 0),
        ["HEM-A1C"] = (6.1, 1.1, 4, 5.6m, 1),
    };

    private static readonly string[] BloodGroups = ["A Rh+", "A Rh-", "B Rh+", "B Rh-", "AB Rh+", "AB Rh-", "0 Rh+", "0 Rh-"];

    public static SyntheticDataset Generate(SyntheticOptions? options = null)
    {
        options ??= new SyntheticOptions();
        var random = new Random(options.Seed);
        var faker = new Faker("tr") { Random = new Randomizer(options.Seed) };
        var catalog = LabCatalog.Default.Tests.OrderBy(t => t.LocalCode, StringComparer.Ordinal).ToArray();

        var patients = new List<HastaRow>(options.Patients);
        var encounters = new List<BasvuruRow>();
        var labs = new List<LabSonucRow>();
        var dirty = new Dictionary<SourceKey, DirtyKind>();
        long encounterId = 0;
        long resultId = 0;

        bool Dirty() => random.NextDouble() < options.DirtyRate;

        for (long p = 1; p <= options.Patients; p++)
        {
            var female = random.Next(2) == 0;
            var birth = DateOnly.FromDateTime(options.Start).AddDays(-random.Next(365 * 1, 365 * 90));
            DateOnly? death = null;
            if (Dirty())
            {
                death = birth.AddDays(-random.Next(1, 400));
                dirty[new SourceKey(SourceTable.Hasta, p)] = DirtyKind.DeathBeforeBirth;
            }
            else if (random.NextDouble() < 0.01)
            {
                death = DateOnly.FromDateTime(options.Start).AddDays(-random.Next(1, 30));
            }

            var place = Places[random.Next(Places.Length)];
            patients.Add(new HastaRow(
                HastaId: p,
                DosyaNo: $"D{p:D8}",
                TcKimlikNo: random.NextDouble() < 0.97 ? TcKimlikNo.Generate(random) : null,
                Ad: faker.Name.FirstName(female ? Bogus.DataSets.Name.Gender.Female : Bogus.DataSets.Name.Gender.Male),
                Soyad: faker.Name.LastName(),
                Cinsiyet: random.NextDouble() < 0.005 ? 'B' : female ? 'K' : 'E',
                DogumTarihi: random.NextDouble() < 0.995 ? birth : null,
                Telefon: random.NextDouble() < 0.9 ? $"05{random.Next(30, 56)}{random.Next(0, 10_000_000):D7}" : null,
                Il: place.Il,
                Ilce: place.Ilce,
                OlumTarihi: death,
                Aktif: death is null));

            var visits = random.Next(1, options.MaxEncountersPerPatient + 1);
            for (var v = 0; v < visits; v++)
            {
                var id = ++encounterId;
                var start = options.Start.AddMinutes(random.Next(0, 60 * 24 * 300));
                var type = random.NextDouble() switch { < 0.7 => 'A', < 0.85 => 'Y', _ => 'C' };
                var status = random.NextDouble() switch { < 0.8 => "TAMAMLANDI", < 0.9 => "DEVAM", < 0.96 => "PLANLANDI", _ => "IPTAL" };
                DateTime? end = status == "TAMAMLANDI"
                    ? start.AddMinutes(type == 'Y' ? random.Next(60 * 24, 60 * 24 * 10) : random.Next(10, 240))
                    : null;
                if (Dirty())
                {
                    status = "TAMAMLANDI";
                    end = null;
                    dirty[new SourceKey(SourceTable.Basvuru, id)] = DirtyKind.FinishedWithoutEnd;
                }

                encounters.Add(new BasvuruRow(id, p, type, status, start, end, Departments[random.Next(Departments.Length)]));

                if (status is "PLANLANDI" or "IPTAL")
                {
                    continue;
                }

                var results = random.Next(0, options.MaxLabResultsPerEncounter + 1);
                for (var r = 0; r < results; r++)
                {
                    var test = catalog[random.Next(catalog.Length)];
                    labs.Add(NewResult(++resultId, id, p, test, start.AddMinutes(30 + random.Next(0, 600)), random, Dirty(), dirty));
                }
            }
        }

        return new SyntheticDataset(patients, encounters, labs, dirty);
    }

    /// <summary>A fresh, valid result for <paramref name="test"/>, used by the dataset and by the live workload.</summary>
    public static LabSonucRow NewResult(long id, long encounterId, long patientId, LabTest test, DateTime at, Random random) =>
        NewResult(id, encounterId, patientId, test, at, random, makeDirty: false, dirty: null);

    private static LabSonucRow NewResult(
        long id, long encounterId, long patientId, LabTest test, DateTime at, Random random, bool makeDirty,
        Dictionary<SourceKey, DirtyKind>? dirty)
    {
        var status = random.NextDouble() switch { < 0.85 => "KESIN", < 0.95 => "ON", _ => "DUZELTILDI" };
        var code = test.LocalCode;
        string? unit = test.UcumUnit is null
            ? null
            : test.AcceptedLocalUnits.Count > 0 && random.NextDouble() < 0.5
                ? test.AcceptedLocalUnits[random.Next(test.AcceptedLocalUnits.Count)]
                : test.UcumUnit;

        if (makeDirty)
        {
            var key = new SourceKey(SourceTable.LabSonuc, id);
            if (random.Next(2) == 0 || test.UcumUnit is null)
            {
                code = "BIY-X" + random.Next(10, 99).ToString(System.Globalization.CultureInfo.InvariantCulture);
                dirty![key] = DirtyKind.UnmappedTestCode;
            }
            else
            {
                unit = test.UcumUnit + "?"; // e.g. "mg/dL?", a typo no catalogue spelling covers
                dirty![key] = DirtyKind.UnknownUnit;
            }
        }

        if (test.UcumUnit is null)
        {
            return new LabSonucRow(id, encounterId, patientId, code, null, BloodGroups[random.Next(BloodGroups.Length)],
                null, null, null, status, at);
        }

        var (mean, sd, low, high, decimals) = Values[test.LocalCode];
        var value = Math.Max(0, mean + (sd * Gaussian(random)));
        return new LabSonucRow(id, encounterId, patientId, code, Math.Round((decimal)value, decimals), null, unit, low, high, status, at);
    }

    private static double Gaussian(Random random)
    {
        // Box-Muller; 1 - NextDouble() keeps the logarithm's argument in (0, 1].
        var u1 = 1.0 - random.NextDouble();
        var u2 = random.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}
