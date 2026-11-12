using System.Text.Json;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;

namespace HbysFhirBridge.Fhir;

/// <summary>One shared System.Text.Json configuration for FHIR R4 (building it reflects over the whole model).</summary>
public static class FhirJson
{
    public static JsonSerializerOptions Options { get; } = new JsonSerializerOptions().ForFhir(ModelInfo.ModelInspector);

    public static string Serialize(Resource resource) => JsonSerializer.Serialize(resource, resource.GetType(), Options);

    public static T Deserialize<T>(string json)
        where T : Resource =>
        JsonSerializer.Deserialize<T>(json, Options) ?? throw new JsonException($"Empty {typeof(T).Name}");
}
