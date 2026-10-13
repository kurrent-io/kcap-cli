using Capacitor.App.ViewModels;

namespace Capacitor.App.Tests.Unit;

/// Pure: no dispatcher, so no session constraint.
public class ToolCardsTests {
    const string PublishResult = """
        {"artefact":{"artefact_id":"01eccca1dfac4da596db71feead98dc8","title":"Retention brief","owner_user_id":"u1","visibility":"org","latest_version":1,"updated_at":"2026-10-07T10:00:00Z","is_owner":true,"url":"https://kurrent.kcap.ai/artefacts/01eccca1dfac4da596db71feead98dc8"},"grants":[],"versions":[],"sources":{"session_ids":[],"hidden_count":0}}
        """;

    [Test]
    public async Task A_publish_result_becomes_a_published_page_card_with_its_link() {
        var card = ToolCards.Build(ToolCardKind.Page, """{"title":"Retention brief","html":"<p>x</p>"}""", PublishResult)!;
        await Assert.That(card.Kind).IsEqualTo(ToolCardKind.Page);
        await Assert.That(card.Title).IsEqualTo("Published page");
        await Assert.That(card.Name).IsEqualTo("Retention brief");
        await Assert.That(card.Meta).IsEqualTo("v1 · Org");
        await Assert.That(card.Url).IsEqualTo("https://kurrent.kcap.ai/artefacts/01eccca1dfac4da596db71feead98dc8");
        await Assert.That(card.DocumentPath).IsNull();
    }

    /// The pages tool hands the server's `artefact` object back as `page`.
    [Test]
    public async Task A_publish_page_result_builds_the_same_card() {
        var card = ToolCards.Build(ToolCardKind.Page, "{}", PublishResult.Replace("\"artefact\":", "\"page\":").Replace("\"artefact_id\":", "\"page_id\":"))!;
        await Assert.That(card.Name).IsEqualTo("Retention brief");
        await Assert.That(card.Url).IsEqualTo("https://kurrent.kcap.ai/artefacts/01eccca1dfac4da596db71feead98dc8");
    }

    [Test]
    public async Task A_second_version_reads_updated_and_a_private_or_scoped_audience_is_named() {
        var v3 = PublishResult.Replace("\"latest_version\":1", "\"latest_version\":3").Replace("\"visibility\":\"org\"", "\"visibility\":\"none\"");
        var card = ToolCards.Build(ToolCardKind.Page, "{}", v3)!;
        await Assert.That(card.Title).IsEqualTo("Updated page");
        await Assert.That(card.Meta).IsEqualTo("v3 · Private");
        await Assert.That(ToolCards.Audience("scoped")).IsEqualTo("Shared");
        await Assert.That(ToolCards.Audience(null)).IsEqualTo("Private");
    }

    /// The server's JSON is passed through verbatim and capped at 4096 characters; a cut body, an
    /// error line or no body at all leaves the call a row.
    [Test]
    [Arguments("Error: not signed in")]
    [Arguments("{\"artefact\":{\"artefact_id\":\"x\",\"title\":\"Retention br")]
    [Arguments("{\"artefact\":{\"title\":\"no url\"}}")]
    [Arguments("")]
    [Arguments(null)]
    public async Task A_result_without_a_whole_artefact_builds_no_page_card(string? result) {
        await Assert.That(ToolCards.Build(ToolCardKind.Page, "{}", result)).IsNull();
    }

    [Test]
    [Arguments("design", "Declared design doc")]
    [Arguments("spec", "Declared spec")]
    [Arguments("plan", "Declared plan")]
    [Arguments("other", "Declared document")]
    public async Task A_declaration_becomes_a_document_card_named_by_file(string kind, string title) {
        var card = ToolCards.Build(ToolCardKind.Document, $$"""{"kind":"{{kind}}","path":"docs/superpowers/specs/2026-10-07-x-design.md"}""", "{\"plan_id\":\"p\"}")!;
        await Assert.That(card.Title).IsEqualTo(title);
        await Assert.That(card.Name).IsEqualTo("2026-10-07-x-design.md");
        await Assert.That(card.Meta).IsEqualTo("docs/superpowers/specs/2026-10-07-x-design.md");
        await Assert.That(card.DocumentPath).IsEqualTo("docs/superpowers/specs/2026-10-07-x-design.md");
        await Assert.That(card.Url).IsNull();
    }

    [Test]
    public async Task A_declaration_without_a_path_builds_no_card_and_file_names_cut_on_either_separator() {
        await Assert.That(ToolCards.Build(ToolCardKind.Document, """{"kind":"plan"}""", "{}")).IsNull();
        await Assert.That(ToolCards.FileName(@"docs\plans\a.md")).IsEqualTo("a.md");
        await Assert.That(ToolCards.FileName("a.md")).IsEqualTo("a.md");
    }

    [Test]
    public async Task Flow_and_agent_cards_come_from_the_arguments_and_carry_no_link() {
        var flow = ToolCards.Build(ToolCardKind.Flow, """{"kind":"code-review","vendor":"codex"}""", "started")!;
        await Assert.That(flow.Title).IsEqualTo("Started code-review flow");
        await Assert.That(flow.Name).IsEqualTo("code-review");
        await Assert.That(flow.Meta).IsEqualTo("codex");
        await Assert.That(flow.Url).IsNull();

        var agent = ToolCards.Build(ToolCardKind.Agent, """{"prompt":"Fix the flaky test\nthen push","vendor":"claude"}""", "ok")!;
        await Assert.That(agent.Title).IsEqualTo("Started hosted agent");
        await Assert.That(agent.Name).IsEqualTo("Fix the flaky test");
        await Assert.That(agent.Meta).IsEqualTo("claude");
        await Assert.That(agent.DocumentPath).IsNull();
    }

    [Test]
    public async Task None_builds_nothing() {
        await Assert.That(ToolCards.Build(ToolCardKind.None, "{}", "{}")).IsNull();
    }
}
