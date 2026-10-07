using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class PageResultKeysTests {
    [Test]
    public async Task Renames_the_artefact_keys_at_every_depth() {
        var renamed = JsonNode.Parse(PageResultKeys.Rename(
            """{"artefacts":[{"artefact_id":"a1","title":"Plan"}],"artefact":{"artefact_id":"a2"}}"""))!;

        await Assert.That((string)renamed["pages"]![0]!["page_id"]!).IsEqualTo("a1");
        await Assert.That((string)renamed["page"]!["page_id"]!).IsEqualTo("a2");
        await Assert.That(renamed.AsObject().ContainsKey("artefacts")).IsFalse();
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
