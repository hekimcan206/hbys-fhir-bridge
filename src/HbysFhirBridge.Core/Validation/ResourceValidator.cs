using System.Text.Json;
using Firely.Fhir.Validation;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Hl7.Fhir.Specification.Source;
using Hl7.Fhir.Specification.Terminology;

namespace HbysFhirBridge.Validation;

/// <summary>One validation finding, flattened from an OperationOutcome issue.</summary>
public sealed record ValidationIssue(string Severity, string Code, string Location, string Message)
{
    public override string ToString() => $"{Severity} {Code} at {Location}: {Message}";
}

public sealed record ValidationOutcome(IReadOnlyList<ValidationIssue> Issues)
{
    public bool IsValid => !Issues.Any(i => i.Severity is "error" or "fatal");

    public IEnumerable<ValidationIssue> Errors => Issues.Where(i => i.Severity is "error" or "fatal");

    /// <summary>A compact, bounded description for logs and the parking table.</summary>
    public string Describe(int maxLength = 3900)
    {
        var text = string.Join(" | ", Errors.Select(e => e.ToString()));
        return text.Length <= maxLength ? text : text[..maxLength];
    }
}

/// <summary>Checks a resource before it leaves the bridge.</summary>
public interface IResourceValidator
{
    ValidationOutcome Validate(Resource resource);
}

/// <summary>
/// Validates resources with the Firely validator against the core R4 definitions plus this project's three
/// profiles (Profiles/*.json). Profiles are written as differentials; snapshots are generated on first use.
/// Building the validator loads the core specification and compiles schemas lazily (about 9 s on the first
/// resource of each type), so create one instance per process and reuse it. Calls are serialised internally.
/// </summary>
public sealed class ResourceValidator : IResourceValidator
{
    private readonly Validator _validator;

    // Concurrent first use of the validator fails with "Invalid snapshot generator state (OnStartRecursion)": the
    // snapshot generator behind lazy profile compilation is not thread-safe (found by the parallel unit tests).
    // Validation is serialised; the sync loop is single-threaded anyway, so this costs nothing there.
    private readonly Lock _gate = new();

    public ResourceValidator()
    {
        var profiles = LoadEmbeddedProfiles();
        var core = ZipSource.CreateValidationSource();
        IAsyncResourceResolver resolver = new CachedResolver(
            new SnapshotSource(new MultiResolver(new InMemoryResourceResolver(profiles), core)));

        // Local terminology only: required bindings to core value sets (status codes, genders) are checked offline.
        // LOINC itself is not shipped with the core spec, so LOINC codes are checked by the profile invariants and
        // the curated lab catalogue rather than by a terminology server (docs/adr/0001).
        var terminology = LocalTerminologyService.CreateDefaultForCore(resolver);
        _validator = new Validator(resolver, terminology, null, new ValidationSettings());
    }

    /// <summary>
    /// Validates one synthetic resource of each type so that their profiles are compiled before real traffic
    /// (the first validation per type takes seconds; later ones about a millisecond, see docs/benchmarks.md).
    /// </summary>
    public void WarmUp()
    {
        var mapper = new Mapping.FhirMapper();
        var start = new DateTime(2026, 1, 5, 9, 0, 0, DateTimeKind.Unspecified);
        Validate(mapper.MapPatient(new Source.HastaRow(1, "D1", null, "Ada", "Deneme", 'K', null, null, null, null, null, true)).Resource!);
        Validate(mapper.MapEncounter(new Source.BasvuruRow(1, 1, 'A', "DEVAM", start, null, null)).Resource!);
        Validate(mapper.MapObservation(new Source.LabSonucRow(1, 1, 1, "BIY-GLU", 90, null, "mg/dL", 70, 100, "KESIN", start)).Resource!);
    }

    public static IReadOnlyList<StructureDefinition> Profiles { get; } = LoadEmbeddedProfiles();

    /// <summary>Validates against the core definition and every profile listed in <c>meta.profile</c>.</summary>
    public ValidationOutcome Validate(Resource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        OperationOutcome outcome;
        lock (_gate)
        {
            outcome = _validator.Validate(resource);
        }

        return new ValidationOutcome(outcome.Issue.Select(ToIssue).ToList());
    }

    private static ValidationIssue ToIssue(OperationOutcome.IssueComponent issue) => new(
        issue.Severity?.ToString().ToLowerInvariant() ?? "error",
        issue.Code?.ToString() ?? "unknown",
        issue.Expression.FirstOrDefault() ?? issue.Location.FirstOrDefault() ?? "",
        issue.Details?.Text ?? issue.Diagnostics ?? "");

    private static List<StructureDefinition> LoadEmbeddedProfiles()
    {
        var assembly = typeof(ResourceValidator).Assembly;
        var options = new JsonSerializerOptions().ForFhir(ModelInfo.ModelInspector);
        var result = new List<StructureDefinition>();
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.Contains(".Profiles.", StringComparison.Ordinal)).Order())
        {
            using var stream = assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"Missing embedded profile {name}");
            result.Add(JsonSerializer.Deserialize<StructureDefinition>(stream, options)
                ?? throw new InvalidOperationException($"Profile {name} is empty"));
        }

        return result;
    }
}
