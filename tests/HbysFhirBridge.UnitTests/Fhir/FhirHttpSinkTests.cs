using System.Net;
using HbysFhirBridge.Bundling;
using HbysFhirBridge.Fhir;
using HbysFhirBridge.Mapping;
using HbysFhirBridge.Source;
using HbysFhirBridge.Sync;
using HbysFhirBridge.Worker;
using Hl7.Fhir.Model;
using static HbysFhirBridge.UnitTests.Support.Rows;

namespace HbysFhirBridge.UnitTests.Fhir;

public sealed class FhirHttpSinkTests
{
    private const string Conflict =
        """{"resourceType":"OperationOutcome","issue":[{"severity":"error","code":"conflict","diagnostics":"Referential integrity violated. Resource `Patient/x` doesn't exist."}]}""";

    private static Bundle OneEntry() => TransactionBundleBuilder.Build(
        [new OutboundChange(Key(SourceTable.Hasta, 1), new FhirMapper().MapPatient(Patient()).Resource)]);

    private static (FhirHttpSink Sink, StubHandler Handler) Create(HttpStatusCode status, string body = "")
    {
        var handler = new StubHandler(status, body);
        return (new FhirHttpSink(new HttpClient(handler) { BaseAddress = new Uri("http://fhir.test/fhir/") }), handler);
    }

    [Fact]
    public async System.Threading.Tasks.Task Success_posts_a_fhir_json_transaction_to_the_base()
    {
        var (sink, handler) = Create(HttpStatusCode.OK, "{}");

        var result = await sink.SendAsync(OneEntry(), TestContext.Current.CancellationToken);

        result.Accepted.Should().BeTrue();
        handler.Request!.Method.Should().Be(HttpMethod.Post);
        handler.Request.RequestUri!.ToString().Should().Be("http://fhir.test/fhir", "Blaze redirects the trailing-slash form with 308");
        handler.ContentType.Should().StartWith("application/fhir+json");
        handler.Body.Should().Contain("\"type\":\"transaction\"").And.Contain("\"method\":\"PUT\"");
    }

    [Fact]
    public async System.Threading.Tasks.Task Conflict_is_a_rejection_with_the_operation_outcome_text()
    {
        var (sink, _) = Create(HttpStatusCode.Conflict, Conflict);

        var result = await sink.SendAsync(OneEntry(), TestContext.Current.CancellationToken);

        result.Should().Be(SinkResult.Reject(409, "Error Conflict: Referential integrity violated. Resource `Patient/x` doesn't exist."));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async System.Threading.Tasks.Task Answers_about_the_server_or_the_caller_are_transient(HttpStatusCode status)
    {
        var (sink, _) = Create(status, "busy");
        var act = () => sink.SendAsync(OneEntry(), TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<FhirServerUnavailableException>();
    }

    [Fact]
    public async System.Threading.Tasks.Task Network_errors_are_transient()
    {
        var sink = new FhirHttpSink(new HttpClient(new ThrowingHandler()) { BaseAddress = new Uri("http://fhir.test/fhir/") });
        var act = () => sink.SendAsync(OneEntry(), TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<FhirServerUnavailableException>();
    }

    [Fact]
    public void Non_outcome_bodies_are_truncated_raw_text() =>
        FhirHttpSink.Describe(new string('x', 900)).Should().HaveLength(500);

    [Fact]
    public void Bundle_orders_deletes_children_first_then_upserts_parents_first()
    {
        var mapper = new FhirMapper();
        var ordered = TransactionBundleBuilder.Order(
        [
            new OutboundChange(Key(SourceTable.LabSonuc, 100), mapper.MapObservation(Lab()).Resource),
            new OutboundChange(Key(SourceTable.Hasta, 9), null),
            new OutboundChange(Key(SourceTable.Hasta, 1), mapper.MapPatient(Patient()).Resource),
            new OutboundChange(Key(SourceTable.LabSonuc, 900), null),
            new OutboundChange(Key(SourceTable.Basvuru, 10), mapper.MapEncounter(Encounter()).Resource),
        ]);

        ordered.Select(o => (o.Key.ToString(), o.IsDelete)).Should().Equal(
            ("LAB_SONUC:900", true), ("HASTA:9", true), ("HASTA:1", false), ("BASVURU:10", false), ("LAB_SONUC:100", false));

        var bundle = TransactionBundleBuilder.Build(ordered);
        bundle.Entry[0].Request.Method.Should().Be(Bundle.HTTPVerb.DELETE);
        bundle.Entry[0].Resource.Should().BeNull();
        bundle.Entry[2].Request.Url.Should().Be(ResourceIds.Reference(SourceTable.Hasta, 1));
        bundle.Entry.Should().OnlyContain(e => e.FullUrl == null);
    }

    [Fact]
    public void Verifier_ignores_server_assigned_meta_only()
    {
        var mapped = new FhirMapper().MapPatient(Patient()).Resource!;
        var fromServer = (Patient)mapped.DeepCopy();
        fromServer.Meta.VersionId = "7";
        fromServer.Meta.LastUpdated = DateTimeOffset.UtcNow;
        ConsistencyVerifier.SameContent(mapped, fromServer).Should().BeTrue();

        fromServer.Telecom.Clear();
        ConsistencyVerifier.SameContent(mapped, fromServer).Should().BeFalse();
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        public string? Body { get; private set; }

        public string? ContentType { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            ContentType = request.Content.Headers.ContentType!.ToString();
            return new HttpResponseMessage(status) { Content = new StringContent(body) };
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("connection refused");
    }
}
