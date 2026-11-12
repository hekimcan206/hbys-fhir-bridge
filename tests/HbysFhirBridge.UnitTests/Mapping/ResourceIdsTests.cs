using FsCheck;
using FsCheck.Xunit;
using HbysFhirBridge.Mapping;
using HbysFhirBridge.Source;

namespace HbysFhirBridge.UnitTests.Mapping;

public sealed class ResourceIdsTests
{
    [Fact]
    public void Uuid_v5_matches_the_well_known_dns_namespace_vector()
    {
        // uuid5(NAMESPACE_DNS, "www.example.com"), the value Python's uuid module and RFC 9562 implementations agree on.
        var dns = new Guid("6ba7b810-9dad-11d1-80b4-00c04fd430c8");
        ResourceIds.UuidV5(dns, "www.example.com").Should().Be(new Guid("2ed6657d-e927-568b-95e1-2665a8aea6a2"));
    }

    [Fact]
    public void Ids_are_stable_across_releases()
    {
        // Changing the namespace or the name format would re-key every resource on the server; this pins both.
        ResourceIds.For(SourceTable.Hasta, 1).Should().Be(ResourceIds.For(new SourceKey(SourceTable.Hasta, 1)));
        ResourceIds.Reference(SourceTable.Hasta, 1).Should().StartWith("Patient/");
        ResourceIds.For(SourceTable.Hasta, 1).Should().Be(PinnedPatient1);
    }

    private static readonly string PinnedPatient1 = ResourceIds.UuidV5(new Guid("6f0b6c1e-6d1f-4c43-9a49-1b8f4f7c2a10"), "HASTA:1").ToString();

    [Property]
    public bool Ids_are_deterministic_version_5_uuids(PositiveInt id)
    {
        var a = Guid.Parse(ResourceIds.For(SourceTable.LabSonuc, id.Get));
        var b = Guid.Parse(ResourceIds.For(SourceTable.LabSonuc, id.Get));
        var text = a.ToString();
        return a == b && text[14] == '5' && "89ab".Contains(text[19], StringComparison.Ordinal);
    }

    [Property]
    public bool Different_rows_never_share_an_id(PositiveInt a, PositiveInt b)
    {
        var tables = new[] { SourceTable.Hasta, SourceTable.Basvuru, SourceTable.LabSonuc };
        var ids = tables.SelectMany(t => new[] { (t, (long)a.Get), (t, (long)b.Get) }).Distinct()
            .Select(k => ResourceIds.For(k.t, k.Item2)).ToList();
        return ids.Count == ids.Distinct().Count();
    }
}
