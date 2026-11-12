using HbysFhirBridge.Fhir;
using HbysFhirBridge.Mapping;
using HbysFhirBridge.Oracle;
using HbysFhirBridge.Source;
using Hl7.Fhir.Model;

namespace HbysFhirBridge.Worker;

public sealed record TypeCount(string ResourceType, int Expected, int OnServer)
{
    public bool Matches => Expected == OnServer;
}

public sealed record VerificationReport(
    IReadOnlyList<TypeCount> Counts,
    int Parked,
    int Compared,
    IReadOnlyList<string> Mismatches)
{
    public bool Consistent => Counts.All(c => c.Matches) && Mismatches.Count == 0;
}

/// <summary>
/// End-to-end check that the FHIR server mirrors the source: for every type, the server holds exactly the rows that
/// exist and are not parked; and a sample of resources read back from the server is deep-equal to what the mapper
/// produces from the current row (server-assigned meta.versionId / meta.lastUpdated ignored).
/// </summary>
public sealed class ConsistencyVerifier(SourceSnapshotReader source, FhirReadClient server, FhirMapper mapper)
{
    public async Task<VerificationReport> VerifyAsync(int sampleSize, int seed = 1, CancellationToken ct = default)
    {
        var (rows, parked) = await source.ReadAsync(ct);
        var expected = rows.Where(r => !parked.Contains(r.Key)).ToList();

        var counts = new List<TypeCount>();
        foreach (var table in new[] { SourceTable.Hasta, SourceTable.Basvuru, SourceTable.LabSonuc })
        {
            var type = SourceTables.ToResourceType(table);
            counts.Add(new TypeCount(type, expected.Count(r => r.Key.Table == table), await server.CountAsync(type, ct)));
        }

        var random = new Random(seed);
        var sample = expected.Count <= sampleSize ? expected : expected.OrderBy(_ => random.Next()).Take(sampleSize).ToList();
        var mismatches = new List<string>();
        foreach (var row in sample)
        {
            var mapped = mapper.Map(row.Key, row.Row!);
            var type = SourceTables.ToResourceType(row.Key.Table);
            var id = ResourceIds.For(row.Key);
            Resource? actual = row.Key.Table switch
            {
                SourceTable.Hasta => await server.ReadAsync<Patient>(type, id, ct),
                SourceTable.Basvuru => await server.ReadAsync<Encounter>(type, id, ct),
                _ => await server.ReadAsync<Observation>(type, id, ct),
            };

            if (actual is null)
            {
                mismatches.Add($"{row.Key}: missing on the server");
            }
            else if (mapped.Resource is null)
            {
                mismatches.Add($"{row.Key}: on the server but no longer mappable ({mapped.FailureReason})");
            }
            else if (!SameContent(mapped.Resource, actual))
            {
                mismatches.Add($"{row.Key}: server copy differs from the current row");
            }
        }

        return new VerificationReport(counts, parked.Count, sample.Count, mismatches);
    }

    internal static bool SameContent(Resource expected, Resource actual)
    {
        var a = (Resource)expected.DeepCopy();
        var b = (Resource)actual.DeepCopy();
        foreach (var r in new[] { a, b })
        {
            if (r.Meta is not null)
            {
                r.Meta.VersionId = null;
                r.Meta.LastUpdated = null;
            }
        }

        return FhirJson.Serialize(a) == FhirJson.Serialize(b);
    }
}
