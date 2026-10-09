using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class PageResultKeysTests {
    [Test]
    public async Task Renames_the_envelope_keys_and_the_page_ids_under_them() {
        var renamed = JsonNode.Parse(PageResultKeys.Rename(
            """{"artefacts":[{"artefact_id":"a1","title":"Plan"}],"artefact":{"artefact_id":"a2"}}"""))!;

        await Assert.That((string)renamed["pages"]![0]!["page_id"]!).IsEqualTo("a1");
        await Assert.That((string)renamed["page"]!["page_id"]!).IsEqualTo("a2");
        await Assert.That(renamed.AsObject().ContainsKey("artefacts")).IsFalse();
    }

    /// An answer's payload is returned as submitted, so a schema field named like a server key stays.
    [Test]
    public async Task Leaves_submitted_answer_payloads_alone() {
        var renamed = JsonNode.Parse(PageResultKeys.Rename(
            """{"responses":[{"response_id":"r1","payload":{"artefact_id":"mine","artefact":{"artefact_id":"x"}}}]}"""))!;

        var payload = renamed["responses"]![0]!["payload"]!.AsObject();
        await Assert.That((string)payload["artefact_id"]!).IsEqualTo("mine");
        await Assert.That((string)payload["artefact"]!["artefact_id"]!).IsEqualTo("x");
        await Assert.That(payload.ContainsKey("page_id")).IsFalse();
    }

    [Test]
    public async Task A_key_whose_new_name_is_taken_keeps_its_old_name() {
        var renamed = JsonNode.Parse(PageResultKeys.Rename(
            """{"pages":[{"artefact_id":"a1","page_id":"p1"}],"artefacts":"kept"}"""))!.AsObject();

        await Assert.That((string)renamed["artefacts"]!).IsEqualTo("kept");
        await Assert.That((string)renamed["pages"]![0]!["artefact_id"]!).IsEqualTo("a1");
        await Assert.That((string)renamed["pages"]![0]!["page_id"]!).IsEqualTo("p1");
    }

    [Test]
    public async Task Leaves_values_and_other_keys_alone() {
        var renamed = JsonNode.Parse(PageResultKeys.Rename("""{"code":"artefact","title":"Migration — plan"}"""))!;

        await Assert.That((string)renamed["code"]!).IsEqualTo("artefact");
        await Assert.That((string)renamed["title"]!).IsEqualTo("Migration — plan");
    }

    [Test]
    public async Task A_body_that_is_not_json_comes_back_unchanged() =>
        await Assert.That(PageResultKeys.Rename("upstream exploded")).IsEqualTo("upstream exploded");
}
