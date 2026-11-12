using System.Net;
using System.Net.Http.Headers;
using System.Text;
using HbysFhirBridge.Sync;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;

namespace HbysFhirBridge.Fhir;

/// <summary>Raised for answers that say nothing about the content (network, 5xx, 429, auth); the batch is retried.</summary>
public sealed class FhirServerUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Posts transaction bundles to a FHIR R4 server. The <see cref="HttpClient"/> is expected to have its
/// <see cref="HttpClient.BaseAddress"/> set to the FHIR base (ending in "/") and may carry a resilience pipeline:
/// every entry is an idempotent PUT or DELETE, so retrying the POST of a whole transaction is safe.
/// </summary>
public sealed class FhirHttpSink(HttpClient http) : IFhirSink
{
    private static readonly MediaTypeHeaderValue FhirJsonType = new("application/fhir+json") { CharSet = "utf-8" };

    public async Task<SinkResult> SendAsync(Bundle transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        using var content = new StringContent(FhirJson.Serialize(transaction), Encoding.UTF8);
        content.Headers.ContentType = FhirJsonType;
        // POST to the base itself, without the trailing slash: Blaze answers "[base]/" with 308, and HttpClient's
        // replay of the redirected body broke the connection ("response ended prematurely") on every later request.
        using var request = new HttpRequestMessage(HttpMethod.Post, TransactionEndpoint(http)) { Content = content };
        request.Headers.Accept.ParseAdd("application/fhir+json");
        // The bridge never reads the response bodies of successful entries; ask for the minimum.
        request.Headers.TryAddWithoutValidation("Prefer", "return=minimal");

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new FhirServerUnavailableException($"FHIR server unreachable: {ex.Message}", ex);
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            if (response.IsSuccessStatusCode)
            {
                return SinkResult.Ok(status);
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (IsContentRejection(response.StatusCode))
            {
                return SinkResult.Reject(status, Describe(body));
            }

            throw new FhirServerUnavailableException($"FHIR server answered {status}: {Describe(body)}");
        }
    }

    internal static Uri TransactionEndpoint(HttpClient client)
    {
        var baseAddress = client.BaseAddress ?? throw new InvalidOperationException("The FHIR HttpClient needs a BaseAddress");
        return new Uri(baseAddress.ToString().TrimEnd('/'));
    }

    /// <summary>4xx answers about the bundle's content. 401/403/408/429 are about the caller or the moment, not the data.</summary>
    internal static bool IsContentRejection(HttpStatusCode status) =>
        (int)status is >= 400 and < 500
        && status is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests);

    /// <summary>Flattens an OperationOutcome body to "severity code: diagnostics" lines; falls back to the raw text.</summary>
    internal static string Describe(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "(empty body)";
        }

        try
        {
            var outcome = FhirJson.Deserialize<OperationOutcome>(body);
            return string.Join(" | ", outcome.Issue.Select(i => $"{i.Severity} {i.Code}: {i.Diagnostics ?? i.Details?.Text}"));
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or DeserializationFailedException or InvalidCastException)
        {
            return body.Length <= 500 ? body : body[..500];
        }
    }
}
