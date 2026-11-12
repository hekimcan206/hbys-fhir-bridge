using BenchmarkDotNet.Attributes;
using HbysFhirBridge.Bundling;
using HbysFhirBridge.Fhir;
using HbysFhirBridge.Mapping;
using HbysFhirBridge.Source;
using HbysFhirBridge.Synthetic;
using HbysFhirBridge.Validation;

namespace HbysFhirBridge.Benchmarks;

/// <summary>Per-row CPU cost of the bridge's pure stages: map, validate, build and serialise a bundle.</summary>
[MemoryDiagnoser]
public class MicroBenchmarks
{
    private readonly FhirMapper _mapper = new();
    private ResourceValidator _validator = null!;
    private HastaRow _patient = null!;
    private LabSonucRow _lab = null!;
    private Hl7.Fhir.Model.Resource _observation = null!;
    private List<OutboundChange> _bundleOf250 = null!;

    [GlobalSetup]
    public void Setup()
    {
        var dataset = SyntheticHospital.Generate(new SyntheticOptions { Patients = 200, DirtyRate = 0 });
        _patient = dataset.Patients[0];
        _lab = dataset.LabResults.First(l => l.SonucDeger is not null);
        _observation = _mapper.MapObservation(_lab).Resource!;
        _validator = new ResourceValidator();
        _validator.Validate(_mapper.MapPatient(_patient).Resource!); // compile profiles outside the measurement
        _validator.Validate(_observation);
        _bundleOf250 = TransactionBundleBuilder.Order(dataset.LabResults.Take(250)
            .Select(l => new OutboundChange(new SourceKey(SourceTable.LabSonuc, l.SonucId), _mapper.MapObservation(l).Resource)));
    }

    [Benchmark]
    public object MapObservation() => _mapper.MapObservation(_lab);

    [Benchmark]
    public object MapPatient() => _mapper.MapPatient(_patient);

    [Benchmark]
    public object ValidateObservation() => _validator.Validate(_observation);

    [Benchmark]
    public object ValidatePatient() => _validator.Validate(_mapper.MapPatient(_patient).Resource!);

    [Benchmark]
    public string BuildAndSerializeBundleOf250() => FhirJson.Serialize(TransactionBundleBuilder.Build(_bundleOf250));

    [Benchmark]
    public string ResourceId() => ResourceIds.For(SourceTable.LabSonuc, 123_456);
}
