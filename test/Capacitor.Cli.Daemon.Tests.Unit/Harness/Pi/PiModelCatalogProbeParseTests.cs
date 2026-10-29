using Capacitor.Cli.Daemon.Harness.Pi;
using TUnit.Assertions.Enums;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Pi;

public class PiModelCatalogProbeParseTests {
    const string Ok = """
        {"type":"response","command":"get_available_models","success":true,"data":{"models":[
          {"id":"claude-opus-5","name":"Claude Opus 5","provider":"anthropic"},
          {"id":"gpt-5.5","name":"GPT-5.5","provider":"github-copilot"},
          {"id":"claude-opus-5","name":"Claude Opus 5","provider":"github-copilot"}]}}
        """;

    [Test]
    public async Task Maps_provider_slash_id_and_name_dot_provider_in_pi_order() {
        var parsed = PiModelCatalogProbe.Parse(Ok.ReplaceLineEndings(""));
        await Assert.That(parsed.IsResponse).IsTrue();
        await Assert.That(parsed.Models!.Select(m => m.Value))
            .IsEquivalentTo(["anthropic/claude-opus-5", "github-copilot/gpt-5.5", "github-copilot/claude-opus-5"], CollectionOrdering.Matching);
        await Assert.That(parsed.Models[0].Label).IsEqualTo("Claude Opus 5 · anthropic");
    }

    [Test]
    public async Task Entry_missing_id_or_provider_or_not_an_object_is_skipped_and_name_falls_back_to_id() {
        var line = """{"type":"response","command":"get_available_models","success":true,"data":{"models":[{"name":"x","provider":"p"},{"id":"a","name":"A"},"junk",{"id":"b","provider":"p"}]}}""";
        var parsed = PiModelCatalogProbe.Parse(line);
        await Assert.That(parsed.Models!.Select(m => m.Value)).IsEquivalentTo(["p/b"]);
        await Assert.That(parsed.Models[0].Label).IsEqualTo("b · p");
    }

    [Test]
    [Arguments("""{"type":"response","command":"get_available_models","success":false}""")]
    [Arguments("""{"type":"response","command":"get_available_models","success":true}""")]
    [Arguments("""{"type":"response","command":"get_available_models","success":true,"data":{}}""")]
    [Arguments("""{"type":"response","command":"get_available_models","success":true,"data":{"models":"nope"}}""")]
    public async Task Invalid_envelope_is_a_response_with_null_models(string line) {
        var parsed = PiModelCatalogProbe.Parse(line);
        await Assert.That(parsed.IsResponse).IsTrue();
        await Assert.That(parsed.Models).IsNull();
    }

    [Test]
    public async Task Empty_models_array_is_an_empty_catalog_not_null() {
        var parsed = PiModelCatalogProbe.Parse("""{"type":"response","command":"get_available_models","success":true,"data":{"models":[]}}""");
        await Assert.That(parsed.Models).IsNotNull();
        await Assert.That(parsed.Models!).IsEmpty();
    }

    [Test]
    [Arguments("""{"type":"response","command":"get_state","success":true}""")]
    [Arguments("""{"type":"ready"}""")]
    [Arguments("[1,2]")]
    [Arguments("not json")]
    [Arguments("")]
    public async Task Other_lines_are_not_the_response(string line) {
        await Assert.That(PiModelCatalogProbe.Parse(line).IsResponse).IsFalse();
    }
}
