using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using HbysFhirBridge.Fhir;
using HbysFhirBridge.Mapping;
using HbysFhirBridge.Source;
using HbysFhirBridge.Sync;
using HbysFhirBridge.UnitTests.Support;
using static HbysFhirBridge.UnitTests.Support.Rows;

namespace HbysFhirBridge.UnitTests.Sync;

/// <summary>
/// Random histories of inserts, updates and deletes (respecting the source foreign keys), interleaved with partial
/// syncs, network failures and crashes between delivery and acknowledgement. After a final clean drain the server
/// must hold exactly the mapped current state of every deliverable row and nothing else.
/// </summary>
public sealed class ConvergencePropertyTests(SharedValidator shared)
{
    // Patient 3 is always recorded with a date of death before birth, so it and everything under it must stay parked.
    private const long BrokenPatient = 3;

    [Property(MaxTest = 60)]
    public Property Server_converges_to_the_deliverable_source_state(int seed) =>
        Prop.ToProperty(RunHistory(seed).GetAwaiter().GetResult());

    private async Task<bool> RunHistory(int seed)
    {
        var random = new Random(seed);
        var source = new InMemorySource();
        var server = new FakeFhirServer();
        var mapper = new FhirMapper();
        SyncEngine Engine() => new(source, server, mapper, shared.Validator,
            new SyncOptions { BatchSize = 1 + random.Next(8), MaxBundleEntries = 1 + random.Next(5) });

        var steps = 20 + random.Next(40);
        for (var step = 0; step < steps; step++)
        {
            Mutate(source, random);
            if (random.Next(4) == 0)
            {
                server.TransientFailures = random.Next(3) == 0 ? 1 : 0;
                source.FailNextComplete = random.Next(6) == 0;
                try
                {
                    await Engine().ProcessNextBatchAsync();
                }
                catch (Exception ex) when (ex is FhirServerUnavailableException or InvalidOperationException)
                {
                    // Nothing was acknowledged; the next run re-delivers.
                }
            }
        }

        server.TransientFailures = 0;
        source.FailNextComplete = false;
        await Engine().DrainAsync();

        var expected = source.Rows
            .Where(r => Deliverable(r.Key, r.Value, source))
            .ToDictionary(r => ResourceIds.Reference(r.Key.Table, r.Key.Id), r => FhirJson.Serialize(mapper.Map(r.Key, r.Value).Resource!));

        return source.Pending == 0
               && server.Resources.Count == expected.Count
               && expected.All(e => server.Resources.TryGetValue(e.Key, out var actual) && actual.Json == e.Value);
    }

    private static bool Deliverable(SourceKey key, object row, InMemorySource source) => row switch
    {
        HastaRow h => h.HastaId != BrokenPatient,
        BasvuruRow b => b.HastaId != BrokenPatient,
        LabSonucRow l => l.HastaId != BrokenPatient && source.Rows.ContainsKey(new SourceKey(SourceTable.Basvuru, l.BasvuruId)),
        _ => false,
    };

    private static void Mutate(InMemorySource source, Random random)
    {
        var patient = 1 + random.Next(4);
        var encounter = (patient * 10) + random.Next(2);
        var lab = (encounter * 10) + random.Next(3);
        var patientKey = new SourceKey(SourceTable.Hasta, patient);
        var encounterKey = new SourceKey(SourceTable.Basvuru, encounter);
        var labKey = new SourceKey(SourceTable.LabSonuc, lab);

        switch (random.Next(7))
        {
            case 0 or 1:
                source.Upsert(patientKey, Patient(patient, patient == BrokenPatient ? new DateOnly(1950, 1, 1) : null) with
                {
                    Telefon = $"0532{random.Next(1_000_000, 9_999_999)}",
                });
                break;
            case 2:
                if (source.Rows.ContainsKey(patientKey))
                {
                    source.Upsert(encounterKey, Encounter(encounter, patient, random.Next(2) == 0 ? "DEVAM" : "TAMAMLANDI"));
                }

                break;
            case 3 or 4:
                if (source.Rows.ContainsKey(encounterKey))
                {
                    source.Upsert(labKey, Lab(lab, encounter, patient, value: random.Next(50, 200)));
                }

                break;
            case 5:
                source.Delete(labKey);
                break;
            default:
                // ON DELETE without cascade in the source: children go first, like the HBYS would have to do it.
                foreach (var child in source.Rows.Keys.Where(k => k.Table == SourceTable.LabSonuc && k.Id / 10 == encounter).ToList())
                {
                    source.Delete(child);
                }

                source.Delete(encounterKey);
                break;
        }
    }
}
