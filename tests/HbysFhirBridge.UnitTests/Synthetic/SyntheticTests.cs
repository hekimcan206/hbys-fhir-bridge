using FsCheck;
using FsCheck.Xunit;
using HbysFhirBridge.Fhir;
using HbysFhirBridge.Synthetic;

namespace HbysFhirBridge.UnitTests.Synthetic;

public sealed class SyntheticTests
{
    [Theory]
    [InlineData("10000000146", true)]
    [InlineData("10000000147", false)]
    [InlineData("01234567890", false)]
    [InlineData("1000000014", false)]
    [InlineData("1000000014a", false)]
    [InlineData(null, false)]
    public void Checksum_rules(string? value, bool valid) => TcKimlikNo.IsValid(value).Should().Be(valid);

    [Property]
    public bool Generated_numbers_are_checksum_valid(int seed) => TcKimlikNo.IsValid(TcKimlikNo.Generate(new Random(seed)));

    [Property]
    public bool Any_single_digit_change_is_detected(int seed, PositiveInt position, PositiveInt delta)
    {
        var value = TcKimlikNo.Generate(new Random(seed)).ToCharArray();
        var i = position.Get % 11;
        var d = 1 + (delta.Get % 9);
        value[i] = (char)('0' + ((value[i] - '0' + d) % 10));
        return !TcKimlikNo.IsValid(new string(value));
    }

    [Fact]
    public void Same_seed_same_dataset()
    {
        var a = SyntheticHospital.Generate(new SyntheticOptions { Patients = 25, Seed = 9 });
        var b = SyntheticHospital.Generate(new SyntheticOptions { Patients = 25, Seed = 9 });
        a.Patients.Should().Equal(b.Patients);
        a.Encounters.Should().Equal(b.Encounters);
        a.LabResults.Should().Equal(b.LabResults);
    }

    [Fact]
    public void Foreign_keys_hold_and_ids_are_unique()
    {
        var data = SyntheticHospital.Generate(new SyntheticOptions { Patients = 120, Seed = 3 });
        var patients = data.Patients.Select(p => p.HastaId).ToHashSet();
        var encounters = data.Encounters.ToDictionary(e => e.BasvuruId);
        patients.Should().HaveCount(120);
        data.Encounters.Should().OnlyContain(e => patients.Contains(e.HastaId));
        data.LabResults.Select(l => l.SonucId).Should().OnlyHaveUniqueItems();
        data.LabResults.Should().OnlyContain(l => encounters.ContainsKey(l.BasvuruId) && encounters[l.BasvuruId].HastaId == l.HastaId);
    }

    [Fact]
    public void Zero_dirty_rate_gives_a_clean_dataset() =>
        SyntheticHospital.Generate(new SyntheticOptions { Patients = 200, DirtyRate = 0 }).Dirty.Should().BeEmpty();

    [Fact]
    public void Fhir_json_round_trips()
    {
        var patient = new HbysFhirBridge.Mapping.FhirMapper().MapPatient(Support.Rows.Patient()).Resource!;
        var json = FhirJson.Serialize(patient);
        FhirJson.Serialize(FhirJson.Deserialize<Hl7.Fhir.Model.Patient>(json)).Should().Be(json);
    }
}
