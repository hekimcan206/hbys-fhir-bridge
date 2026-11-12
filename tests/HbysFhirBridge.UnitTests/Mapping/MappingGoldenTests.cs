using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using HbysFhirBridge.Fhir;
using HbysFhirBridge.Mapping;
using HbysFhirBridge.Source;
using static HbysFhirBridge.UnitTests.Support.Rows;

namespace HbysFhirBridge.UnitTests.Mapping;

/// <summary>
/// Golden (approval) tests: each case's FHIR JSON is compared with a reviewed file under Golden/. A mapping change
/// shows up as a readable JSON diff in review. Set UPDATE_GOLDEN=1 to rewrite the files after an intended change.
/// </summary>
public sealed class MappingGoldenTests
{
    // Relaxed escaping keeps "+03:00" and Turkish letters readable in the reviewed files.
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static TheoryData<string> Cases => [.. GoldenCases.All.Keys.Order(StringComparer.Ordinal)];

    [Theory]
    [MemberData(nameof(Cases))]
    public void Mapped_resource_matches_its_golden_file(string name)
    {
        var (key, row, options) = GoldenCases.All[name];
        var result = new FhirMapper(LabCatalog.Default, options).Map(key, row);
        result.Succeeded.Should().BeTrue(result.FailureDetail);

        var actual = JsonNode.Parse(FhirJson.Serialize(result.Resource!))!.ToJsonString(Pretty).ReplaceLineEndings("\n") + "\n";
        var path = Path.Combine(AppContext.BaseDirectory, "Golden", name + ".json");

        if (Environment.GetEnvironmentVariable("UPDATE_GOLDEN") == "1")
        {
            var sourcePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Golden", name + ".json"));
            File.WriteAllText(sourcePath, actual);
            return;
        }

        File.Exists(path).Should().BeTrue($"golden file {name}.json exists (run once with UPDATE_GOLDEN=1 to create it)");
        actual.Should().Be(File.ReadAllText(path).ReplaceLineEndings("\n"));
    }
}

internal static class GoldenCases
{
    public static readonly Dictionary<string, (SourceKey Key, object Row, MappingOptions Options)> All = new()
    {
        ["patient-full"] = (Key(SourceTable.Hasta, 1), Patient(1), new MappingOptions()),
        ["patient-with-national-id"] = (Key(SourceTable.Hasta, 1), Patient(1), new MappingOptions { IncludeNationalId = true }),
        ["patient-minimal-deceased"] = (Key(SourceTable.Hasta, 2),
            new HastaRow(2, " D00000002 ", null, "Mehmet", "Kaya", 'B', null, null, null, null, new DateOnly(2026, 2, 1), false), new MappingOptions()),
        ["encounter-finished-outpatient"] = (Key(SourceTable.Basvuru, 10), Encounter(10), new MappingOptions()),
        ["encounter-inpatient-in-progress"] = (Key(SourceTable.Basvuru, 11),
            new BasvuruRow(11, 1, 'Y', "DEVAM", Morning, null, "KAR"), new MappingOptions()),
        ["encounter-emergency-cancelled"] = (Key(SourceTable.Basvuru, 12),
            new BasvuruRow(12, 1, 'C', "IPTAL", Morning, null, null), new MappingOptions()),
        ["observation-high-glucose-local-unit"] = (Key(SourceTable.LabSonuc, 100), Lab(100), new MappingOptions()),
        ["observation-low-potassium"] = (Key(SourceTable.LabSonuc, 101),
            Lab(101, code: "BIY-K", value: 3.1000m, unit: "mEq/L") with { RefAlt = 3.5m, RefUst = 5.1m }, new MappingOptions()),
        ["observation-tsh-corrected"] = (Key(SourceTable.LabSonuc, 102),
            Lab(102, code: "HOR-TSH", value: 2.4m, unit: "uIU/mL") with { RefAlt = 0.27m, RefUst = 4.2m, Durum = "DUZELTILDI" }, new MappingOptions()),
        ["observation-blood-group-text"] = (Key(SourceTable.LabSonuc, 103),
            new LabSonucRow(103, 10, 1, "KAN-ABO", null, "0 Rh+", null, null, null, "KESIN", Morning.AddHours(2)), new MappingOptions()),
        ["observation-final-without-value"] = (Key(SourceTable.LabSonuc, 104),
            Lab(104, value: null, unit: null), new MappingOptions()),
        ["observation-preliminary-no-range"] = (Key(SourceTable.LabSonuc, 105),
            Lab(105, code: "HEM-WBC", value: 7.20m, unit: "x10^3/uL") with { RefAlt = null, RefUst = null, Durum = "ON" }, new MappingOptions()),
    };
}
