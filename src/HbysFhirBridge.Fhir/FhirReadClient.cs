using System.Net;
using System.Text.Json;
using Hl7.Fhir.Model;

namespace HbysFhirBridge.Fhir;

/// <summary>Reads back from the FHIR server for verification, tests and the demo (counts, single resources, history).</summary>
public sealed class FhirReadClient(HttpClient http)
{
    /// <summary>Number of current (non-deleted) resources of a type, via <c>_summary=count</c>.</summary>
    public async Task<int> CountAsync(string resourceType, CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync($"{resourceType}?_summary=count", ct);
        return doc.RootElement.GetProperty("total").GetInt32();
    }

    /// <summary>Total versions ever written for a type (the history count), used to prove that replays write nothing.</summary>
    public async Task<int> HistoryCountAsync(string resourceType, CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync($"{resourceType}/_history?_summary=count", ct);
        return doc.RootElement.GetProperty("total").GetInt32();
    }

    /// <summary>The resource, or null when it does not exist or was deleted (404 / 410).</summary>
    public async Task<T?> ReadAsync<T>(string resourceType, string id, CancellationToken ct = default)
        where T : Resource
    {
        using var response = await http.GetAsync($"{resourceType}/{id}", ct);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return FhirJson.Deserialize<T>(await response.Content.ReadAsStringAsync(ct));
    }

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
    }
}
