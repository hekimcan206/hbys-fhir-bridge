using System.Security.Cryptography;
using System.Text;
using HbysFhirBridge.Source;

namespace HbysFhirBridge.Mapping;

/// <summary>
/// Deterministic FHIR logical ids: a name-based UUID (RFC 9562 version 5) of the source table and primary key.
/// The same row always gets the same id, so every send is an idempotent <c>PUT [type]/[id]</c> and a retry or a
/// replay can never create a duplicate. Internal sequence numbers do not appear in URLs (docs/adr/0003).
/// </summary>
public static class ResourceIds
{
    // A fixed, project-specific namespace. Changing it would re-key every resource on the server.
    private static readonly Guid Namespace = new("6f0b6c1e-6d1f-4c43-9a49-1b8f4f7c2a10");

    public static string For(SourceKey key) => For(key.Table, key.Id);

    public static string For(SourceTable table, long id) =>
        UuidV5(Namespace, $"{SourceTables.ToDbName(table)}:{id.ToString(System.Globalization.CultureInfo.InvariantCulture)}")
            .ToString();

    public static string Reference(SourceTable table, long id) => $"{SourceTables.ToResourceType(table)}/{For(table, id)}";

    internal static Guid UuidV5(Guid namespaceId, string name)
    {
        Span<byte> ns = stackalloc byte[16];
        namespaceId.TryWriteBytes(ns, bigEndian: true, out _);

        var nameBytes = Encoding.UTF8.GetBytes(name);
        var input = new byte[16 + nameBytes.Length];
        ns.CopyTo(input);
        nameBytes.CopyTo(input, 16);

        // SHA-1 is what RFC 9562 specifies for version 5; it is used here for naming, not for security.
#pragma warning disable CA5350
        var hash = SHA1.HashData(input);
#pragma warning restore CA5350

        hash[6] = (byte)((hash[6] & 0x0F) | 0x50); // version 5
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80); // RFC 4122/9562 variant
        return new Guid(hash.AsSpan(0, 16), bigEndian: true);
    }
}
