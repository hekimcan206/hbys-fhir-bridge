using HbysFhirBridge.Fhir;
using HbysFhirBridge.Sync;
using Hl7.Fhir.Model;
using Task = System.Threading.Tasks.Task;

namespace HbysFhirBridge.UnitTests.Support;

/// <summary>
/// A FHIR server model with the behaviour that matters to the bridge, as observed on Blaze 1.12: transactions are
/// atomic; referential integrity is checked against the state after the whole transaction (409 otherwise), for
/// upserts and for deletes; an identical PUT does not create a new version; DELETE of a missing resource succeeds.
/// </summary>
internal sealed class FakeFhirServer : IFhirSink
{
    private Dictionary<string, (string Json, int Version)> _resources = new(StringComparer.Ordinal);

    public int Sends { get; private set; }

    public int VersionsWritten { get; private set; }

    /// <summary>Number of upcoming sends that fail as if the network dropped.</summary>
    public int TransientFailures { get; set; }

    /// <summary>Content the server refuses (simulates server-side validation), keyed by "Type/id".</summary>
    public HashSet<string> Refuse { get; } = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, (string Json, int Version)> Resources => _resources;

    public Task<SinkResult> SendAsync(Bundle transaction, CancellationToken cancellationToken)
    {
        Sends++;
        if (TransientFailures > 0)
        {
            TransientFailures--;
            throw new FhirServerUnavailableException("simulated network failure");
        }

        var next = new Dictionary<string, (string Json, int Version)>(_resources, StringComparer.Ordinal);
        var written = 0;
        foreach (var entry in transaction.Entry.Where(e => e.Request.Method == Bundle.HTTPVerb.DELETE))
        {
            next.Remove(entry.Request.Url);
        }

        foreach (var entry in transaction.Entry.Where(e => e.Request.Method == Bundle.HTTPVerb.PUT))
        {
            var url = entry.Request.Url;
            if (Refuse.Contains(url))
            {
                return Task.FromResult(SinkResult.Reject(422, $"{url} refused"));
            }

            var json = FhirJson.Serialize(entry.Resource);
            if (next.TryGetValue(url, out var existing))
            {
                if (existing.Json != json)
                {
                    next[url] = (json, existing.Version + 1);
                    written++;
                }
            }
            else
            {
                next[url] = (json, 1);
                written++;
            }
        }

        foreach (var (url, value) in next)
        {
            foreach (var reference in References(FhirJson.Deserialize<Resource>(value.Json)))
            {
                if (!next.ContainsKey(reference))
                {
                    return Task.FromResult(SinkResult.Reject(409, $"Referential integrity violated. Resource `{reference}` doesn't exist (from {url})."));
                }
            }
        }

        _resources = next;
        VersionsWritten += written;
        return Task.FromResult(SinkResult.Ok());
    }

    public bool Has(string url) => _resources.ContainsKey(url);

    public T Get<T>(string url)
        where T : Resource => FhirJson.Deserialize<T>(_resources[url].Json);

    private static IEnumerable<string> References(Resource resource) => resource switch
    {
        Encounter e => [e.Subject.Reference],
        Observation o => [o.Subject.Reference, o.Encounter.Reference],
        _ => [],
    };
}
