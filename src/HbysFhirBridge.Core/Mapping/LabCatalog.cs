using System.Collections.Frozen;

namespace HbysFhirBridge.Mapping;

/// <summary>One local laboratory test and its standard coding.</summary>
/// <param name="LocalCode">TETKIK_KODU as the HBYS stores it.</param>
/// <param name="LocalName">The display name the laboratory uses (Turkish).</param>
/// <param name="Loinc">LOINC code.</param>
/// <param name="LoincDisplay">LOINC long common name.</param>
/// <param name="UcumUnit">UCUM unit code, or null for a non-numeric result.</param>
/// <param name="AcceptedLocalUnits">Unit spellings found in BIRIM that mean exactly <paramref name="UcumUnit"/>.</param>
public sealed record LabTest(
    string LocalCode,
    string LocalName,
    string Loinc,
    string LoincDisplay,
    string? UcumUnit,
    IReadOnlyList<string> AcceptedLocalUnits);

/// <summary>
/// Local test code to LOINC/UCUM map (a ConceptMap in code). Units are only ever recognised, never converted: a
/// result reported in a unit the map does not list is parked for a human instead of being silently rescaled.
/// </summary>
public sealed class LabCatalog
{
    private readonly FrozenDictionary<string, LabTest> _byLocalCode;

    public LabCatalog(IEnumerable<LabTest> tests)
    {
        ArgumentNullException.ThrowIfNull(tests);
        _byLocalCode = tests.ToFrozenDictionary(t => t.LocalCode, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<LabTest> Tests => _byLocalCode.Values;

    public bool TryGet(string localCode, out LabTest test) => _byLocalCode.TryGetValue(localCode.Trim(), out test!);

    /// <summary>Resolves a BIRIM value to the test's UCUM code; false when the spelling is not known to mean it.</summary>
    public static bool TryResolveUnit(LabTest test, string? localUnit, out string ucum)
    {
        ArgumentNullException.ThrowIfNull(test);
        ucum = test.UcumUnit ?? string.Empty;
        if (test.UcumUnit is null || string.IsNullOrWhiteSpace(localUnit))
        {
            return false;
        }

        var unit = localUnit.Trim();
        return string.Equals(unit, test.UcumUnit, StringComparison.Ordinal)
            || test.AcceptedLocalUnits.Contains(unit, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The synthetic hospital's catalogue. LOINC codes are real; the local codes are invented.</summary>
    public static LabCatalog Default { get; } = new(
    [
        new("BIY-GLU", "Glukoz (aclik)", "2345-7", "Glucose [Mass/volume] in Serum or Plasma", "mg/dL", ["mg/dl"]),
        new("BIY-KRE", "Kreatinin", "2160-0", "Creatinine [Mass/volume] in Serum or Plasma", "mg/dL", ["mg/dl"]),
        new("BIY-ALT", "ALT", "1742-6", "Alanine aminotransferase [Enzymatic activity/volume] in Serum or Plasma", "U/L", ["IU/L", "U/l"]),
        new("BIY-NA", "Sodyum", "2951-2", "Sodium [Moles/volume] in Serum or Plasma", "mmol/L", ["mmol/l", "mEq/L"]),
        new("BIY-K", "Potasyum", "2823-3", "Potassium [Moles/volume] in Serum or Plasma", "mmol/L", ["mmol/l", "mEq/L"]),
        new("BIY-CRP", "CRP", "1988-5", "C reactive protein [Mass/volume] in Serum or Plasma", "mg/L", ["mg/l"]),
        new("HOR-TSH", "TSH", "3016-3", "Thyrotropin [Units/volume] in Serum or Plasma", "m[IU]/L", ["mIU/L", "uIU/mL", "mU/L"]),
        new("HEM-HGB", "Hemoglobin", "718-7", "Hemoglobin [Mass/volume] in Blood", "g/dL", ["g/dl"]),
        new("HEM-WBC", "Lokosit (WBC)", "6690-2", "Leukocytes [#/volume] in Blood by Automated count", "10*3/uL", ["10^3/uL", "10^3/µL", "x10^3/uL"]),
        new("HEM-PLT", "Trombosit (PLT)", "777-3", "Platelets [#/volume] in Blood by Automated count", "10*3/uL", ["10^3/uL", "10^3/µL", "x10^3/uL"]),
        new("HEM-A1C", "HbA1c", "4548-4", "Hemoglobin A1c/Hemoglobin.total in Blood", "%", []),
        new("KAN-ABO", "Kan grubu", "882-1", "ABO and Rh group [Type] in Blood", null, []),
    ]);
}
