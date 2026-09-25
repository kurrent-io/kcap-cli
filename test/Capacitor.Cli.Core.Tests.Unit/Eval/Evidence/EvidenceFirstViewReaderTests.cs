using System.Text.Json;
using Capacitor.Cli.Core.Eval.Evidence;

namespace Capacitor.Cli.Core.Tests.Unit.Eval.Evidence;

/// <summary>A built first view becomes seeded pages continuing the orientation's handles, with its strategy stamps and its
/// guidance; the server's first-view vector reads as one turns page naming its omitted section; an unavailable, failed or
/// unreadable answer is no view; a refused or moved scope is reported by its status.</summary>
public class EvidenceFirstViewReaderTests : IDisposable {
    readonly EvidenceServerStub _stub = new();
    readonly HttpClient _http = new();

    public void Dispose() { _http.Dispose(); _stub.Dispose(); }

    static string Root => EvidenceServerStub.RootSource;

    EvidenceReadClient Reader() => new(_http, _stub.Url, EvidenceServerStub.SessionId);

    static string Built() =>
        "{\"state\":\"built\",\"requested_strategy\":\"completion\",\"strategy\":\"completion\",\"strategy_version\":\"completion-v1\",\"guidance\":\"Judge each obligation separately.\","
      + "\"budget_bytes\":196608,\"sections\":[{\"purpose\":\"closing_events\",\"kind\":\"events\",\"operation\":\"ReadEventsAsync\",\"budget_bytes\":65536,\"turns\":null,\"events\":"
      + EvidenceServerStub.EventsPage(Root, [EvidenceServerStub.EventEntry(Root, 0, "last words")], next: "NEXT") + ",\"calls\":null,\"authorizations\":null}],\"omitted_sections\":[],\"limitations\":[]}";

    [Test]
    public async Task A_built_view_is_seeded_pages_after_the_orientation_with_its_stamps() {
        _stub.Route("GET", "evidence-first-view", 200, Built(), new Dictionary<string, string> { ["token"] = "tok", ["strategy"] = "completion", ["budget_bytes"] = "196608" });

        var (view, failed, answered) = await EvidenceFirstViewReader.ReadAsync(Reader(), "tok", "completion", 3, CancellationToken.None);

        await Assert.That(failed).IsNull();
        await Assert.That(answered).IsTrue();
        await Assert.That(view!.Strategy).IsEqualTo("completion");
        await Assert.That(view.StrategyVersion).IsEqualTo("completion-v1");
        var page = view.Pages.Single();
        await Assert.That(page.Handle).IsEqualTo("o3");
        await Assert.That(page.Tool).IsEqualTo("read_events");
        await Assert.That(page.Cites["o3.1"]).IsEqualTo($"{Root}@0");
        await Assert.That(page.Next).IsEqualTo("NEXT");
        await Assert.That(view.Text.Contains("Judge each obligation separately.")).IsTrue();
        await Assert.That(view.Text.Contains("closing_events")).IsTrue();
        await Assert.That(view.Text.Contains(page.Text)).IsTrue();
    }

    [Test]
    public async Task The_server_first_view_vector_reads_as_one_turns_page_and_names_its_omitted_section() {
        _stub.Route("GET", "evidence-first-view", 200, File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "eval-strategies", "first-view-response.json")));

        var (view, _, _) = await EvidenceFirstViewReader.ReadAsync(Reader(), "tok", "completion", 2, CancellationToken.None);

        var page = view!.Pages.Single();
        await Assert.That(page.Handle).IsEqualTo("o2");
        await Assert.That(page.Tool).IsEqualTo("list_turns");
        using var args = JsonDocument.Parse(page.ArgsJson);
        await Assert.That(args.RootElement.GetProperty("section").GetString()).IsEqualTo("opening_turns");
        await Assert.That(view.Text.Contains("AgentSession-abc@0-41")).IsTrue();
    }

    [Test]
    public async Task An_unavailable_view_is_no_view_and_a_refused_or_moved_scope_is_reported() {
        _stub.Route("GET", "evidence-first-view", 200, """{"state":"unavailable","strategy":"general","strategy_version":"general-v1","sections":[]}""");
        var (none, noneFailed, noneAnswered) = await EvidenceFirstViewReader.ReadAsync(Reader(), "tok", "safety", 3, CancellationToken.None);
        await Assert.That(none).IsNull();
        await Assert.That(noneFailed).IsNull();
        await Assert.That(noneAnswered).IsTrue();

        _stub.Route("GET", "evidence-first-view", 409, """{"code":"scope_moved","current_version":"v2"}""", priority: 1);
        var (_, moved, _) = await EvidenceFirstViewReader.ReadAsync(Reader(), "tok", "safety", 3, CancellationToken.None);
        await Assert.That(moved).IsEqualTo(409);

        _stub.Route("GET", "evidence-first-view", 404, "", priority: 0);
        var (_, gone, _) = await EvidenceFirstViewReader.ReadAsync(Reader(), "tok", "safety", 3, CancellationToken.None);
        await Assert.That(gone).IsEqualTo(404);
    }

    [Test]
    public async Task A_failed_or_unreadable_answer_is_no_view_and_not_an_answer() {
        _stub.Route("GET", "evidence-first-view", 500, "boom");
        var (failedView, failedStatus, failedAnswered) = await EvidenceFirstViewReader.ReadAsync(Reader(), "tok", "safety", 3, CancellationToken.None);
        await Assert.That(failedView).IsNull();
        await Assert.That(failedStatus).IsNull();
        await Assert.That(failedAnswered).IsFalse();

        _stub.Route("GET", "evidence-first-view", 200, "not json", priority: 1);
        var (view, failed, answered) = await EvidenceFirstViewReader.ReadAsync(Reader(), "tok", "safety", 3, CancellationToken.None);
        await Assert.That(view).IsNull();
        await Assert.That(failed).IsNull();
        await Assert.That(answered).IsFalse();
    }
}
