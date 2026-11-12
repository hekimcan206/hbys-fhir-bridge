using HbysFhirBridge.Mapping;
using HbysFhirBridge.Source;
using Hl7.Fhir.Model;

namespace HbysFhirBridge.Bundling;

/// <summary>One entry to send: an upsert (resource present) or a delete (resource null).</summary>
public sealed record OutboundChange(SourceKey Key, Resource? Resource)
{
    public bool IsDelete => Resource is null;

    public string Url => ResourceIds.Reference(Key.Table, Key.Id);
}

/// <summary>
/// Builds FHIR transaction bundles of idempotent entries only: <c>PUT [type]/[id]</c> and <c>DELETE [type]/[id]</c>
/// with deterministic ids. Re-sending a bundle therefore converges to the same server state, which is what lets the
/// source side use at-least-once delivery (docs/adr/0003).
/// </summary>
public static class TransactionBundleBuilder
{
    /// <summary>
    /// The order the entries are sent in. A FHIR transaction is atomic and its result must not depend on entry
    /// order, but the bisecting retry splits a list into halves that are sent one after another, so the list order
    /// matters there: deletes go first, children before parents (a server that enforces referential integrity
    /// refuses to delete a Patient its Encounters still point at), then upserts, parents before children.
    /// </summary>
    public static List<OutboundChange> Order(IEnumerable<OutboundChange> changes) =>
        changes
            .OrderBy(c => c.IsDelete ? 0 : 1)
            .ThenBy(c => c.IsDelete ? -SourceTables.DependencyRank(c.Key.Table) : SourceTables.DependencyRank(c.Key.Table))
            .ThenBy(c => c.Key.Id)
            .ToList();

    /// <summary>Builds a transaction bundle; <paramref name="ordered"/> is expected to come from <see cref="Order"/>.</summary>
    public static Bundle Build(IReadOnlyList<OutboundChange> ordered)
    {
        ArgumentNullException.ThrowIfNull(ordered);
        var bundle = new Bundle { Type = Bundle.BundleType.Transaction };
        foreach (var change in ordered)
        {
            bundle.Entry.Add(change.IsDelete
                ? new Bundle.EntryComponent
                {
                    Request = new Bundle.RequestComponent { Method = Bundle.HTTPVerb.DELETE, Url = change.Url },
                }
                : new Bundle.EntryComponent
                {
                    // No fullUrl: references are plain "Patient/{id}" relative references that resolve against the
                    // server base, and a urn:uuid fullUrl would invite servers to rewrite them.
                    Resource = change.Resource,
                    Request = new Bundle.RequestComponent { Method = Bundle.HTTPVerb.PUT, Url = change.Url },
                });
        }

        return bundle;
    }
}
