using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class McpToolArgumentsTests {
    static JsonObject Args(string json) => JsonNode.Parse(json)!.AsObject();

    [Test]
    public async Task Optional_string_is_null_for_absent_null_or_blank() {
        await Assert.That(McpToolArguments.OptionalString(Args("{}"), "k")).IsNull();
        await Assert.That(McpToolArguments.OptionalString(Args("""{"k":null}"""), "k")).IsNull();
        await Assert.That(McpToolArguments.OptionalString(Args("""{"k":"  "}"""), "k")).IsNull();
        await Assert.That(McpToolArguments.OptionalString(null, "k")).IsNull();
    }

    [Test]
    public async Task Optional_string_trims_and_rejects_a_non_string() {
        await Assert.That(McpToolArguments.OptionalString(Args("""{"k":" v "}"""), "k")).IsEqualTo("v");
        await Assert.That(() => McpToolArguments.OptionalString(Args("""{"k":7}"""), "k"))
            .Throws<ArgumentException>().WithMessageContaining("'k' must be a string");
    }

    [Test]
    public async Task Require_string_rejects_missing_blank_and_wrong_type() {
        await Assert.That(() => McpToolArguments.RequireString(Args("{}"), "k")).Throws<ArgumentException>().WithMessageContaining("required");
        await Assert.That(() => McpToolArguments.RequireString(Args("""{"k":" "}"""), "k")).Throws<ArgumentException>().WithMessageContaining("blank");
        await Assert.That(() => McpToolArguments.RequireString(Args("""{"k":[]}"""), "k")).Throws<ArgumentException>().WithMessageContaining("string");
    }

    [Test]
    public async Task Try_read_int_reads_wire_integers_and_rejects_other_shapes() {
        await Assert.That(McpToolArguments.TryReadInt(Args("""{"n":3}"""), "n", out var n)).IsTrue();
        await Assert.That(n).IsEqualTo(3);
        await Assert.That(McpToolArguments.TryReadInt(Args("{}"), "n", out _)).IsFalse();
        await Assert.That(McpToolArguments.TryReadInt(Args("""{"n":null}"""), "n", out _)).IsFalse();
        await Assert.That(() => McpToolArguments.TryReadInt(Args("""{"n":"3"}"""), "n", out _)).Throws<ArgumentException>();
        await Assert.That(() => McpToolArguments.TryReadInt(Args("""{"n":1.5}"""), "n", out _)).Throws<ArgumentException>();
    }
}
