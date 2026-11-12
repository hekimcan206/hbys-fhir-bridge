using HbysFhirBridge.Mapping;
using HbysFhirBridge.Source;
using Hl7.Fhir.Model;
using static HbysFhirBridge.UnitTests.Support.Rows;

namespace HbysFhirBridge.UnitTests.Mapping;

public sealed class FhirMapperTests
{
    private readonly FhirMapper _mapper = new();

    [Fact]
    public void National_id_is_not_exported_unless_enabled()
    {
        var off = (Patient)_mapper.MapPatient(Patient()).Resource!;
        var on = (Patient)new FhirMapper(LabCatalog.Default, new MappingOptions { IncludeNationalId = true }).MapPatient(Patient()).Resource!;

        off.Identifier.Select(i => i.System).Should().NotContain(Systems.TcKimlikNo);
        on.Identifier.Should().Contain(i => i.System == Systems.TcKimlikNo && i.Value == "10000000146");
    }

    [Theory]
    [InlineData('E', AdministrativeGender.Male)]
    [InlineData('K', AdministrativeGender.Female)]
    [InlineData('B', AdministrativeGender.Unknown)]
    public void Gender_codes_map_to_administrative_gender(char cinsiyet, AdministrativeGender expected) =>
        ((Patient)_mapper.MapPatient(Patient() with { Cinsiyet = cinsiyet }).Resource!).Gender.Should().Be(expected);

    [Fact]
    public void Source_timestamps_carry_the_turkey_offset()
    {
        var encounter = (Encounter)_mapper.MapEncounter(Encounter()).Resource!;
        encounter.Period.Start.Should().Be("2026-03-02T09:30:00+03:00");
    }

    [Fact]
    public void Oracle_trailing_zeros_do_not_change_the_serialised_value()
    {
        var a = (Observation)_mapper.MapObservation(Lab(value: 5.6000m)).Resource!;
        var b = (Observation)_mapper.MapObservation(Lab(value: 5.6m)).Resource!;
        ((Quantity)a.Value).Value?.ToString(System.Globalization.CultureInfo.InvariantCulture).Should().Be("5.6");
        HbysFhirBridge.Fhir.FhirJson.Serialize(a).Should().Be(HbysFhirBridge.Fhir.FhirJson.Serialize(b));
    }

    [Theory]
    [InlineData(65, "L")]
    [InlineData(85, "N")]
    [InlineData(140, "H")]
    public void Interpretation_follows_the_reference_range(decimal value, string code) =>
        ((Observation)_mapper.MapObservation(Lab(value: value)).Resource!).Interpretation.Single().Coding.Single().Code.Should().Be(code);

    [Fact]
    public void Unknown_test_codes_and_units_are_failures_not_guesses()
    {
        _mapper.MapObservation(Lab(code: "BIY-XYZ")).FailureReason.Should().Be(MappingFailureReasons.UnmappedCode);
        _mapper.MapObservation(Lab(unit: "mmol/L")).FailureReason.Should().Be(MappingFailureReasons.UnknownUnit, "glucose in mmol/L would need conversion, which the bridge never does silently");
        _mapper.MapObservation(Lab(unit: null)).FailureReason.Should().Be(MappingFailureReasons.UnknownUnit);
    }

    [Theory]
    [InlineData("BEKLIYOR")]
    [InlineData("")]
    public void Unknown_status_values_are_invalid_source(string durum)
    {
        _mapper.MapEncounter(Encounter() with { Durum = durum }).FailureReason.Should().Be(MappingFailureReasons.InvalidSource);
        _mapper.MapObservation(Lab() with { Durum = durum }).FailureReason.Should().Be(MappingFailureReasons.InvalidSource);
    }

    [Fact]
    public void Unknown_visit_type_is_invalid_source() =>
        _mapper.MapEncounter(Encounter() with { BasvuruTipi = 'X' }).FailureReason.Should().Be(MappingFailureReasons.InvalidSource);

    [Fact]
    public void References_use_the_deterministic_ids_of_the_parent_rows()
    {
        var observation = (Observation)_mapper.MapObservation(Lab(100, 10, 1)).Resource!;
        observation.Subject.Reference.Should().Be(ResourceIds.Reference(SourceTable.Hasta, 1));
        observation.Encounter.Reference.Should().Be(ResourceIds.Reference(SourceTable.Basvuru, 10));
    }

    [Fact]
    public void Map_dispatches_on_the_row_type_and_rejects_others()
    {
        _mapper.Map(Key(SourceTable.Hasta, 1), Patient()).Resource.Should().BeOfType<Patient>();
        var act = () => _mapper.Map(Key(SourceTable.Hasta, 1), "not a row");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Every_catalogue_entry_has_a_loinc_code_and_its_unit_spellings_resolve()
    {
        foreach (var test in LabCatalog.Default.Tests)
        {
            test.Loinc.Should().MatchRegex(@"^\d{1,5}-\d$");
            foreach (var spelling in test.AcceptedLocalUnits)
            {
                LabCatalog.TryResolveUnit(test, spelling, out var ucum).Should().BeTrue();
                ucum.Should().Be(test.UcumUnit);
            }
        }
    }
}
