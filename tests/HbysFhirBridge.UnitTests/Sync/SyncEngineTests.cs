using HbysFhirBridge.Fhir;
using HbysFhirBridge.Mapping;
using HbysFhirBridge.Source;
using HbysFhirBridge.Sync;
using HbysFhirBridge.UnitTests.Support;
using Hl7.Fhir.Model;
using static HbysFhirBridge.UnitTests.Support.Rows;

namespace HbysFhirBridge.UnitTests.Sync;

public sealed class SyncEngineTests(SharedValidator shared)
{
    private readonly InMemorySource _source = new();
    private readonly FakeFhirServer _server = new();

    private SyncEngine Engine(bool holdChildren = true, int batchSize = 500, int bundle = 250) =>
        new(_source, _server, new FhirMapper(), shared.Validator,
            new SyncOptions { BatchSize = batchSize, MaxBundleEntries = bundle, HoldChildrenOfParkedParents = holdChildren });

    private static string Url(SourceTable table, long id) => ResourceIds.Reference(table, id);

    private void SeedVisit(long patient, long encounter, params long[] labs)
    {
        _source.Upsert(Key(SourceTable.Hasta, patient), Patient(patient));
        _source.Upsert(Key(SourceTable.Basvuru, encounter), Encounter(encounter, patient));
        foreach (var lab in labs)
        {
            _source.Upsert(Key(SourceTable.LabSonuc, lab), Lab(lab, encounter, patient));
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task Delivers_every_row_and_consumes_the_journal()
    {
        SeedVisit(1, 10, 100, 101);

        var report = await Engine().DrainAsync(TestContext.Current.CancellationToken);

        report.Delivered.Should().Be(4);
        report.Parked.Should().Be(0);
        _source.Pending.Should().Be(0);
        _server.Resources.Should().HaveCount(4);
        _server.Has(Url(SourceTable.LabSonuc, 101)).Should().BeTrue();
    }

    [Fact]
    public async System.Threading.Tasks.Task Several_changes_to_one_row_in_a_batch_are_sent_once_with_the_latest_state()
    {
        SeedVisit(1, 10);
        _source.Upsert(Key(SourceTable.Hasta, 1), Patient(1) with { Telefon = "05559998877" });

        var report = await Engine().DrainAsync(TestContext.Current.CancellationToken);

        report.Delivered.Should().Be(2);
        _server.Get<Patient>(Url(SourceTable.Hasta, 1)).Telecom.Single().Value.Should().Be("05559998877");
    }

    [Fact]
    public async System.Threading.Tasks.Task An_invalid_parent_is_parked_and_holds_its_children_without_a_round_trip()
    {
        _source.Upsert(Key(SourceTable.Hasta, 1), Patient(1, death: new DateOnly(1970, 1, 1)));
        _source.Upsert(Key(SourceTable.Basvuru, 10), Encounter(10, 1));
        _source.Upsert(Key(SourceTable.LabSonuc, 100), Lab(100, 10, 1));
        SeedVisit(2, 20, 200);

        var report = await Engine().DrainAsync(TestContext.Current.CancellationToken);

        report.Delivered.Should().Be(3);
        _source.Parked[Key(SourceTable.Hasta, 1)].Reason.Should().Be(ParkReasons.Invalid);
        _source.Parked[Key(SourceTable.Hasta, 1)].Detail.Should().Contain("hbys-pat-2");
        _source.Parked[Key(SourceTable.Basvuru, 10)].Should().Be(
            new ParkedRow(Key(SourceTable.Basvuru, 10), ParkReasons.Dependency, "waits for HASTA:1 (parked)", Key(SourceTable.Hasta, 1)));
        _source.Parked[Key(SourceTable.LabSonuc, 100)].BlockedBy.Should().Be(Key(SourceTable.Basvuru, 10));
        _server.Sends.Should().Be(1, "held children never reach the server");
    }

    [Fact]
    public async System.Threading.Tasks.Task Without_holding_children_the_server_rejection_is_isolated_by_bisection()
    {
        _source.Upsert(Key(SourceTable.Hasta, 1), Patient(1, death: new DateOnly(1970, 1, 1)));
        _source.Upsert(Key(SourceTable.Basvuru, 10), Encounter(10, 1));
        for (var p = 2; p <= 9; p++)
        {
            SeedVisit(p, p * 10);
        }

        var report = await Engine(holdChildren: false).DrainAsync(TestContext.Current.CancellationToken);

        report.Delivered.Should().Be(16);
        var parkedChild = _source.Parked[Key(SourceTable.Basvuru, 10)];
        parkedChild.Reason.Should().Be(ParkReasons.Dependency);
        parkedChild.BlockedBy.Should().Be(Key(SourceTable.Hasta, 1));
        parkedChild.Detail.Should().Contain("HTTP 409").And.Contain("Referential integrity");
        _server.Sends.Should().BeGreaterThan(1).And.BeLessThanOrEqualTo(1 + (2 * (int)Math.Ceiling(Math.Log2(17))));
    }

    [Fact]
    public async System.Threading.Tasks.Task Fixing_a_parked_parent_releases_its_waiting_children_in_the_same_run()
    {
        _source.Upsert(Key(SourceTable.Hasta, 1), Patient(1, death: new DateOnly(1970, 1, 1)));
        _source.Upsert(Key(SourceTable.Basvuru, 10), Encounter(10, 1));
        _source.Upsert(Key(SourceTable.LabSonuc, 100), Lab(100, 10, 1));
        await Engine().DrainAsync(TestContext.Current.CancellationToken);
        _source.Parked.Should().HaveCount(3);

        _source.Upsert(Key(SourceTable.Hasta, 1), Patient(1)); // the data steward fixes the date of death
        var report = await Engine().DrainAsync(TestContext.Current.CancellationToken);

        report.Delivered.Should().Be(3, "the patient, then the encounter it released, then the lab result that released");
        _source.Parked.Should().BeEmpty();
        _server.Resources.Should().HaveCount(3);
    }

    [Fact]
    public async System.Threading.Tasks.Task A_server_side_refusal_parks_only_that_resource()
    {
        SeedVisit(1, 10, 100, 101, 102);
        _server.Refuse.Add(Url(SourceTable.LabSonuc, 101));

        var report = await Engine().DrainAsync(TestContext.Current.CancellationToken);

        report.Delivered.Should().Be(4);
        _source.Parked.Keys.Should().Equal(Key(SourceTable.LabSonuc, 101));
        _source.Parked[Key(SourceTable.LabSonuc, 101)].Reason.Should().Be(ParkReasons.Rejected);
    }

    [Fact]
    public async System.Threading.Tasks.Task Mapping_failures_are_parked_with_their_reason()
    {
        SeedVisit(1, 10);
        _source.Upsert(Key(SourceTable.LabSonuc, 100), Lab(100, code: "BIY-XYZ"));
        _source.Upsert(Key(SourceTable.LabSonuc, 101), Lab(101, unit: "mmol/dL"));

        await Engine().DrainAsync(TestContext.Current.CancellationToken);

        _source.Parked[Key(SourceTable.LabSonuc, 100)].Reason.Should().Be(MappingFailureReasons.UnmappedCode);
        _source.Parked[Key(SourceTable.LabSonuc, 101)].Reason.Should().Be(MappingFailureReasons.UnknownUnit);
    }

    [Fact]
    public async System.Threading.Tasks.Task A_transient_failure_acknowledges_nothing_and_the_retry_converges()
    {
        SeedVisit(1, 10, 100);
        _server.TransientFailures = 1;

        var act = () => Engine().DrainAsync(TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<FhirServerUnavailableException>();
        _source.Pending.Should().Be(3);

        await Engine().DrainAsync(TestContext.Current.CancellationToken);
        _source.Pending.Should().Be(0);
        _server.Resources.Should().HaveCount(3);
    }

    [Fact]
    public async System.Threading.Tasks.Task A_crash_after_delivery_replays_the_batch_without_writing_new_versions()
    {
        SeedVisit(1, 10, 100, 101);
        _source.FailNextComplete = true;

        var act = () => Engine().DrainAsync(TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<InvalidOperationException>();
        _server.Resources.Should().HaveCount(4, "the server accepted the bundle before the crash");
        _source.Pending.Should().Be(4, "but the journal was not consumed");
        var versionsBefore = _server.VersionsWritten;

        await Engine().DrainAsync(TestContext.Current.CancellationToken);

        _source.Pending.Should().Be(0);
        _server.VersionsWritten.Should().Be(versionsBefore, "deterministic ids + identical content make the replay a no-op");
    }

    [Fact]
    public async System.Threading.Tasks.Task Deleting_a_visit_with_its_results_succeeds_under_referential_integrity()
    {
        SeedVisit(1, 10, 100, 101);
        await Engine().DrainAsync(TestContext.Current.CancellationToken);

        _source.Delete(Key(SourceTable.LabSonuc, 100));
        _source.Delete(Key(SourceTable.LabSonuc, 101));
        _source.Delete(Key(SourceTable.Basvuru, 10));
        var report = await Engine(bundle: 1).DrainAsync(TestContext.Current.CancellationToken);

        report.Delivered.Should().Be(3);
        report.Parked.Should().Be(0, "children are deleted before parents even when every entry is its own bundle");
        _server.Resources.Keys.Should().Equal(Url(SourceTable.Hasta, 1));
    }

    [Fact]
    public async System.Threading.Tasks.Task Large_backlogs_are_processed_in_bounded_batches()
    {
        for (var p = 1; p <= 30; p++)
        {
            SeedVisit(p, p * 10, p * 100);
        }

        var report = await Engine(batchSize: 25, bundle: 10).DrainAsync(TestContext.Current.CancellationToken);

        report.Batches.Should().Be(4);
        report.Delivered.Should().Be(90);
        _source.Completed.Should().Be(4);
    }
}
