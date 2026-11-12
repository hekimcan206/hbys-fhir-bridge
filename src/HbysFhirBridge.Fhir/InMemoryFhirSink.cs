using System.Collections.Concurrent;
using HbysFhirBridge.Sync;
using Hl7.Fhir.Model;

namespace HbysFhirBridge.Fhir;

/// <summary>
/// Accepts every transaction and remembers the last state sent per URL (JSON, or null after a DELETE). The capture
/// experiment runs two change sources side by side against one of these each, so that what a strategy delivered can
/// be compared with the source without a FHIR server in the measurement.
/// </summary>
public sealed class InMemoryFhirSink : IFhirSink
{
    private readonly ConcurrentDictionary<string, string?> _state = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, string?> State => _state;

    public int Bundles { get; private set; }

    public Task<SinkResult> SendAsync(Bundle transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        foreach (var entry in transaction.Entry)
        {
            _state[entry.Request!.Url!] = entry.Request.Method == Bundle.HTTPVerb.DELETE ? null : FhirJson.Serialize(entry.Resource!);
        }

        Bundles++;
        return System.Threading.Tasks.Task.FromResult(SinkResult.Ok());
    }
}
