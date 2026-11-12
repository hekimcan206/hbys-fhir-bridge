using HbysFhirBridge.Mapping;
using HbysFhirBridge.Synthetic;
using HbysFhirBridge.UnitTests.Support;
using HbysFhirBridge.Validation;
using Hl7.Fhir.Model;
using static HbysFhirBridge.UnitTests.Support.Rows;

namespace HbysFhirBridge.UnitTests.Validation;

public sealed class ResourceValidatorTests(SharedValidator shared)
{
    private readonly ResourceValidator _validator = shared.Validator;
    private readonly FhirMapper _mapper = new();

    [Fact]
    public void Well_formed_resources_of_each_type_are_valid()
    {
        foreach (var resource in new Resource[]
                 {
                     _mapper.MapPatient(Patient()).Resource!, _mapper.MapEncounter(Encounter()).Resource!,
                     _mapper.MapObservation(Lab()).Resource!,
                 })
        {
            var outcome = _validator.Validate(resource);
            outcome.IsValid.Should().BeTrue(outcome.Describe());
        }
    }

    [Fact]
    public void Death_before_birth_violates_hbys_pat_2() =>
        Errors(_mapper.MapPatient(Patient(death: new DateOnly(1980, 1, 1))).Resource!).Should().Contain("hbys-pat-2");

    [Fact]
    public void Finished_visit_without_end_violates_hbys_enc_1()
    {
        var encounter = (Encounter)_mapper.MapEncounter(Encounter()).Resource!;
        encounter.Period.End = null;
        Errors(encounter).Should().Contain("hbys-enc-1");
    }

    [Fact]
    public void Visit_ending_before_it_starts_violates_hbys_enc_2() =>
        Errors(_mapper.MapEncounter(Encounter(end: Morning.AddHours(-1))).Resource!).Should().Contain("hbys-enc-2");

    [Fact]
    public void Observation_without_loinc_violates_hbys_obs_1()
    {
        var observation = (Observation)_mapper.MapObservation(Lab()).Resource!;
        observation.Code.Coding.RemoveAll(c => c.System == Systems.Loinc);
        Errors(observation).Should().Contain("hbys-obs-1");
    }

    [Fact]
    public void Released_observation_without_value_or_reason_violates_hbys_obs_3()
    {
        var observation = (Observation)_mapper.MapObservation(Lab()).Resource!;
        observation.Value = null;
        observation.ReferenceRange.Clear();
        observation.Interpretation.Clear();
        Errors(observation).Should().Contain("hbys-obs-3");
    }

    [Fact]
    public void Quantity_without_ucum_violates_hbys_obs_4()
    {
        var observation = (Observation)_mapper.MapObservation(Lab()).Resource!;
        ((Quantity)observation.Value).System = "urn:local:units";
        Errors(observation).Should().Contain("hbys-obs-4");
    }

    [Fact]
    public void Core_required_bindings_are_enforced_offline()
    {
        // Blaze 1.12 accepted gender "bogus" with 201 Created (docs/adr/0004); the bridge must not rely on the server.
        var patient = (Patient)_mapper.MapPatient(Patient()).Resource!;
        patient.GenderElement = new Code<AdministrativeGender> { JsonValue = "bogus" };
        _validator.Validate(patient).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Missing_profile_required_element_is_an_error()
    {
        var patient = (Patient)_mapper.MapPatient(Patient()).Resource!;
        patient.Gender = null;
        _validator.Validate(patient).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Every_clean_synthetic_row_maps_to_a_valid_resource_and_every_dirty_row_is_caught()
    {
        var dataset = SyntheticHospital.Generate(new SyntheticOptions { Patients = 60, DirtyRate = 0.05, Seed = 42 });
        dataset.Dirty.Should().NotBeEmpty();

        foreach (var (key, row) in dataset.AllRows())
        {
            var mapped = _mapper.Map(key, row);
            var valid = mapped.Succeeded && _validator.Validate(mapped.Resource!).IsValid;
            var dirty = dataset.Dirty.GetValueOrDefault(key);
            valid.Should().Be(dirty == DirtyKind.None, $"{key} is {dirty} ({mapped.FailureDetail})");
            if (dirty == DirtyKind.UnmappedTestCode)
            {
                mapped.FailureReason.Should().Be(MappingFailureReasons.UnmappedCode);
            }
            else if (dirty == DirtyKind.UnknownUnit)
            {
                mapped.FailureReason.Should().Be(MappingFailureReasons.UnknownUnit);
            }
        }
    }

    [Fact]
    public void The_three_profiles_are_embedded()
    {
        ResourceValidator.Profiles.Select(p => p.Url).Should().BeEquivalentTo(
            Systems.PatientProfile, Systems.EncounterProfile, Systems.LabObservationProfile);
    }

    private string Errors(Resource resource) => _validator.Validate(resource).Describe();
}
