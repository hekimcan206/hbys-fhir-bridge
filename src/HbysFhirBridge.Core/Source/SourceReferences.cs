namespace HbysFhirBridge.Source;

/// <summary>The rows a source row points at (the resources its FHIR resource references), nearest parent first.</summary>
public static class SourceReferences
{
    public static IReadOnlyList<SourceKey> Of(object row) => row switch
    {
        HastaRow => [],
        BasvuruRow b => [new SourceKey(SourceTable.Hasta, b.HastaId)],
        // Nearest parent first: a result waits on its visit, which in turn waits on its patient.
        LabSonucRow l => [new SourceKey(SourceTable.Basvuru, l.BasvuruId), new SourceKey(SourceTable.Hasta, l.HastaId)],
        _ => throw new ArgumentException($"Unsupported row type {row?.GetType().Name}", nameof(row)),
    };
}
