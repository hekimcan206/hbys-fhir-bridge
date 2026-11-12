using HbysFhirBridge.Fhir;
using HbysFhirBridge.Mapping;
using HbysFhirBridge.Oracle;
using HbysFhirBridge.Source;
using HbysFhirBridge.Synthetic;
using HbysFhirBridge.Worker;
using Hl7.Fhir.Model;
using Oracle.ManagedDataAccess.Client;

namespace HbysFhirBridge.IntegrationTests;

/// <summary>Oracle 23ai Free (source) and Blaze 1.12 (FHIR server), driven through the real journal source and HTTP sink.</summary>
public sealed class BridgeEndToEndTests(BridgeFixture fx) : IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await fx.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async System.Threading.Tasks.Task Initial_load_with_dirty_rows_mirrors_the_source_and_parks_the_rest()
    {
        var dataset = SyntheticHospital.Generate(new SyntheticOptions { Patients = 60, DirtyRate = 0.05, Seed = 11 });
        (await OracleSeeder.SeedAsync(fx.OwnerConnectionString, dataset, Ct)).Should().BeTrue();

        var report = await fx.Engine().DrainAsync(Ct);

        report.Rows.Should().Be(dataset.RowCount);
        var status = await new BridgeStatusReader(fx.SourceOptions()).ReadAsync(Ct);
        status.PendingChanges.Should().Be(0);
        status.Parked.Should().Be(report.Parked);

        var (_, parked) = await new SourceSnapshotReader(fx.SourceOptions()).ReadAsync(Ct);
        parked.Should().Contain(dataset.Dirty.Keys, "every deliberately broken row is parked");
        // Children of broken parents are parked as dependencies, everything else is delivered.
        var expectedParked = ExpectedParked(dataset);
        parked.Should().BeEquivalentTo(expectedParked);

        var verification = await new ConsistencyVerifier(new SourceSnapshotReader(fx.SourceOptions()), fx.Reader(), new FhirMapper())
            .VerifyAsync(sampleSize: 150, ct: Ct);
        verification.Mismatches.Should().BeEmpty();
        verification.Consistent.Should().BeTrue(string.Join(", ", verification.Counts));
    }

    [Fact]
    public async System.Threading.Tasks.Task Updates_and_deletes_reach_the_server()
    {
        await SeedVisitAsync(patient: 1, encounter: 10, labs: [100, 101]);
        await fx.Engine().DrainAsync(Ct);

        await fx.ExecuteAsOwnerAsync(
            "UPDATE hasta SET telefon = '05550001122' WHERE hasta_id = 1",
            "UPDATE lab_sonuc SET sonuc_deger = 61, durum = 'DUZELTILDI' WHERE sonuc_id = 100",
            "DELETE FROM lab_sonuc WHERE sonuc_id = 101");
        var report = await fx.Engine().DrainAsync(Ct);

        report.Delivered.Should().Be(3);
        var reader = fx.Reader();
        (await reader.ReadAsync<Patient>("Patient", ResourceIds.For(SourceTable.Hasta, 1), Ct))!.Telecom.Single().Value.Should().Be("05550001122");
        var corrected = await reader.ReadAsync<Observation>("Observation", ResourceIds.For(SourceTable.LabSonuc, 100), Ct);
        corrected!.Status.Should().Be(ObservationStatus.Corrected);
        corrected.Interpretation.Single().Coding.Single().Code.Should().Be("L");
        (await reader.ReadAsync<Observation>("Observation", ResourceIds.For(SourceTable.LabSonuc, 101), Ct)).Should().BeNull("the deleted result is gone (410)");
    }

    [Fact]
    public async System.Threading.Tasks.Task Replaying_unchanged_rows_writes_no_new_versions_on_the_server()
    {
        await SeedVisitAsync(patient: 2, encounter: 20, labs: [200, 201, 202]);
        await fx.Engine().DrainAsync(Ct);
        var reader = fx.Reader();
        var before = await reader.HistoryCountAsync("Observation", Ct) + await reader.HistoryCountAsync("Patient", Ct);

        // No-op updates still fire the capture triggers: the bridge re-sends identical resources with the same ids.
        await fx.ExecuteAsOwnerAsync("UPDATE hasta SET telefon = telefon", "UPDATE lab_sonuc SET durum = durum");
        var report = await fx.Engine().DrainAsync(Ct);

        report.Delivered.Should().Be(4);
        var after = await reader.HistoryCountAsync("Observation", Ct) + await reader.HistoryCountAsync("Patient", Ct);
        after.Should().Be(before, "PUT of identical content to the same id creates no version on Blaze");
    }

    [Fact]
    public async System.Threading.Tasks.Task Fixing_a_parked_patient_releases_its_visit_and_results()
    {
        await SeedVisitAsync(patient: 3, encounter: 30, labs: [300, 301], brokenPatient: true);
        var first = await fx.Engine().DrainAsync(Ct);
        first.Parked.Should().Be(4);
        first.BundlesSent.Should().Be(0, "nothing deliverable: the patient is invalid and the rest waits for it");

        await fx.ExecuteAsOwnerAsync("UPDATE hasta SET olum_tarihi = NULL, aktif = 'E' WHERE hasta_id = 3");
        var second = await fx.Engine().DrainAsync(Ct);

        second.Delivered.Should().Be(4);
        (await new BridgeStatusReader(fx.SourceOptions()).ReadAsync(Ct)).Parked.Should().Be(0);
        (await fx.Reader().ReadAsync<Observation>("Observation", ResourceIds.For(SourceTable.LabSonuc, 301), Ct)).Should().NotBeNull();
    }

    [Fact]
    public async System.Threading.Tasks.Task A_long_transaction_is_lost_by_the_timestamp_strategy_but_not_by_the_journal()
    {
        await SeedVisitAsync(patient: 4, encounter: 40, labs: []);
        await SeedVisitAsync(patient: 5, encounter: 50, labs: []);
        var journalSink = new InMemoryFhirSink();
        var naiveSink = new InMemoryFhirSink();
        var journal = fx.Engine(new OracleJournalSource(fx.SourceOptions()), journalSink);
        var naive = fx.Engine(new TimestampWatermarkSource(fx.SourceOptions("naive")), naiveSink);
        await journal.DrainAsync(Ct);
        await naive.DrainAsync(Ct);

        // Writer A stamps patient 4 first but commits last; writer B stamps patient 5 later and commits at once.
        await using var a = new OracleConnection(fx.OwnerConnectionString);
        await a.OpenAsync(Ct);
        var txA = a.BeginTransaction();
        await ExecAsync(a, "UPDATE hasta SET telefon = '05000000004' WHERE hasta_id = 4");
        await System.Threading.Tasks.Task.Delay(50, Ct);
        await fx.ExecuteAsOwnerAsync("UPDATE hasta SET telefon = '05000000005' WHERE hasta_id = 5", "DELETE FROM basvuru WHERE basvuru_id = 50");

        await journal.DrainAsync(Ct);
        await naive.DrainAsync(Ct); // sees patient 5 and moves its watermark past A's earlier stamp
        await txA.CommitAsync(Ct);
        await journal.DrainAsync(Ct);
        await naive.DrainAsync(Ct);

        var patient4 = ResourceIds.Reference(SourceTable.Hasta, 4);
        var encounter50 = ResourceIds.Reference(SourceTable.Basvuru, 50);
        journalSink.State[patient4].Should().Contain("05000000004");
        journalSink.State[encounter50].Should().BeNull("the journal carries deletes");
        naiveSink.State[patient4].Should().NotContain("05000000004", "the late-committing update was skipped for good");
        naiveSink.State[encounter50].Should().NotBeNull("a deleted row leaves nothing for a timestamp query to find");
    }

    [Fact]
    public async System.Threading.Tasks.Task A_second_bridge_instance_cannot_take_a_batch_while_one_is_open()
    {
        await SeedVisitAsync(patient: 6, encounter: 60, labs: []);
        var first = new OracleJournalSource(fx.SourceOptions());
        var second = new OracleJournalSource(fx.SourceOptions());

        await using (var batch = await first.OpenBatchAsync(100, Ct))
        {
            batch.Should().NotBeNull();
            var act = () => second.OpenBatchAsync(100, Ct);
            await act.Should().ThrowAsync<PipelineBusyException>();
        }

        // The open batch was disposed without completing: its changes are still pending and the lease is free.
        (await fx.Engine().DrainAsync(Ct)).Delivered.Should().Be(2);
    }

    [Fact]
    public async System.Threading.Tasks.Task The_bridge_login_cannot_change_clinical_data_or_forge_journal_entries()
    {
        await SeedVisitAsync(patient: 7, encounter: 70, labs: []);
        await using var connection = new OracleConnection(fx.BridgeConnectionString);
        await connection.OpenAsync(Ct);

        foreach (var sql in new[]
                 {
                     "UPDATE hbys.hasta SET telefon = NULL WHERE hasta_id = 7",
                     "DELETE FROM hbys.basvuru WHERE basvuru_id = 70",
                     "INSERT INTO hbys.degisiklik_gunlugu (tablo_adi, kayit_id, islem) VALUES ('HASTA', 7, 'U')",
                 })
        {
            var act = () => ExecAsync(connection, sql);
            (await act.Should().ThrowAsync<OracleException>()).Which.Number.Should().BeOneOf(1031, 41900); // 23ai reports the specific missing privilege as ORA-41900
        }
    }

    private async System.Threading.Tasks.Task SeedVisitAsync(long patient, long encounter, long[] labs, bool brokenPatient = false)
    {
        var start = new DateTime(2026, 4, 1, 10, 0, 0, DateTimeKind.Unspecified);
        var test = LabCatalog.Default.Tests.Single(t => t.LocalCode == "BIY-GLU");
        await using var connection = new OracleConnection(fx.OwnerConnectionString);
        await connection.OpenAsync(Ct);
        await using var tx = connection.BeginTransaction();
        await OracleSeeder.InsertPatientsAsync(connection,
        [
            new HastaRow(patient, $"D{patient:D8}", null, "Deniz", "Test", 'E', new DateOnly(1990, 1, 1), "05321112233", "Ankara", "Çankaya",
                brokenPatient ? new DateOnly(1980, 1, 1) : null, !brokenPatient),
        ], Ct);
        await OracleSeeder.InsertEncountersAsync(connection, [new BasvuruRow(encounter, patient, 'A', "TAMAMLANDI", start, start.AddMinutes(20), "DAH")], Ct);
        if (labs.Length > 0)
        {
            await OracleSeeder.InsertLabResultsAsync(connection,
                labs.Select(l => SyntheticHospital.NewResult(l, encounter, patient, test, start.AddMinutes(30), new Random((int)l)) with { Durum = "KESIN" }).ToList(), Ct);
        }

        await tx.CommitAsync(Ct);
    }

    private static async System.Threading.Tasks.Task ExecAsync(OracleConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Ct);
    }

    private static HashSet<SourceKey> ExpectedParked(SyntheticDataset dataset)
    {
        var parked = dataset.Dirty.Keys.ToHashSet();
        foreach (var e in dataset.Encounters.Where(e => parked.Contains(new SourceKey(SourceTable.Hasta, e.HastaId))))
        {
            parked.Add(new SourceKey(SourceTable.Basvuru, e.BasvuruId));
        }

        foreach (var l in dataset.LabResults.Where(l => parked.Contains(new SourceKey(SourceTable.Basvuru, l.BasvuruId))))
        {
            parked.Add(new SourceKey(SourceTable.LabSonuc, l.SonucId));
        }

        return parked;
    }
}
