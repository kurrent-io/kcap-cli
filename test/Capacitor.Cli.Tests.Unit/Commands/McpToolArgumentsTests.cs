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

    [Test]
    public async Task Try_read_long_reads_wire_integers_and_canonical_integer_strings() {
        await Assert.That(McpToolArguments.TryReadLong(Args("""{"n":9007199254740993}"""), "n", out var big)).IsTrue();
        await Assert.That(big).IsEqualTo(9007199254740993L);
        await Assert.That(McpToolArguments.TryReadLong(Args("""{"n":"42"}"""), "n", out var text)).IsTrue();
        await Assert.That(text).IsEqualTo(42L);
        await Assert.That(McpToolArguments.TryReadLong(Args("""{"n":"-0"}"""), "n", out var negativeZero)).IsTrue();
        await Assert.That(negativeZero).IsEqualTo(0L);
        await Assert.That(McpToolArguments.TryReadLong(new JsonObject { ["n"] = 7L }, "n", out var built)).IsTrue();
        await Assert.That(built).IsEqualTo(7L);
        await Assert.That(McpToolArguments.TryReadLong(Args("{}"), "n", out _)).IsFalse();
        await Assert.That(McpToolArguments.TryReadLong(Args("""{"n":null}"""), "n", out _)).IsFalse();
    }

    [Test]
    [Arguments("""{"n":1.5}""")]
    [Arguments("""{"n":"007"}""")]
    [Arguments("""{"n":"+1"}""")]
    [Arguments("""{"n":" 1"}""")]
    [Arguments("""{"n":""}""")]
    [Arguments("""{"n":"99999999999999999999"}""")]
    [Arguments("""{"n":true}""")]
    [Arguments("""{"n":[1]}""")]
    public async Task Try_read_long_rejects_every_other_present_shape(string json) {
        await Assert.That(() => McpToolArguments.TryReadLong(Args(json), "n", out _)).Throws<ArgumentException>();
    }
}
