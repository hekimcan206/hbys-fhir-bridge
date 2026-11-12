namespace HbysFhirBridge.Mapping;

public sealed class MappingOptions
{
    /// <summary>
    /// Export the national identity number. Off by default: KVKK (the Turkish data protection law) requires data
    /// minimisation, and the target only needs it when it matches patients across institutions (docs/threat-model.md).
    /// </summary>
    public bool IncludeNationalId { get; set; }

    /// <summary>
    /// UTC offset of the HBYS TIMESTAMP columns, which carry no zone. Turkey has used a fixed +03:00 all year since
    /// 2016, so a fixed offset is exact there and avoids depending on tzdata in the chiseled runtime image.
    /// </summary>
    public TimeSpan SourceUtcOffset { get; set; } = TimeSpan.FromHours(3);
}
