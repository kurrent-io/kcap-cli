using System.Text;
using System.Text.Json;
using Capacitor.Cli.Core.Eval.Evidence;

namespace Capacitor.Cli.Core.Tests.Unit.Eval.Evidence;

/// <summary>The obligation array a judge writes is accepted only within every cap: the maximal array encodes to exactly the
/// bound, and one entry, byte or token over, a blank title, an unknown word or member, or malformed JSON drops the whole
/// array. The server's golden parse vectors hold.</summary>
public class EvalObligationContractTests {
    static string Quote(string s) => "\"" + JsonEncodedText.Encode(s) + "\"";

    static string Entry(string title = "t", string origin = "plan", string status = "verified", string anchor = "p1.1", string citations = "[\"p1.2\"]", string? note = null) =>
        $$"""{"title":{{Quote(title)}},"origin":"{{origin}}","status":"{{status}}","anchor":"{{anchor}}","citations":{{citations}}{{(note is null ? "" : ",\"note\":" + Quote(note))}} }""";

    static IReadOnlyList<EvalReportedObligation>? Parse(string json) {
        using var doc = JsonDocument.Parse(json);
        return EvalObligationContract.TryParse(doc.RootElement);
    }

    static string MaximalEntry() =>
        $$"""{"title":"{{new string('t', 256)}}","origin":"scope_change","status":"unverified","anchor":"{{new string('a', 16)}}","citations":["{{new string('c', 16)}}","{{new string('c', 16)}}","{{new string('c', 16)}}","{{new string('c', 16)}}"],"note":"{{new string('n', 256)}}"}""";

    [Test]
    public async Task The_maximal_valid_array_encodes_to_exactly_the_bound() {
        var array = "[" + string.Join(",", Enumerable.Repeat(MaximalEntry(), 50)) + "]";

        await Assert.That(Encoding.UTF8.GetByteCount(array)).IsEqualTo(EvalObligationContract.MaxEncodedBytes);
        await Assert.That(Parse(array)!.Count).IsEqualTo(50);
    }

    [Test]
    public async Task A_valid_entry_parses_with_its_tokens() {
        var parsed = Parse("[" + Entry(note: "n") + "]")!.Single();

        await Assert.That(parsed.Anchor).IsEqualTo("p1.1");
        await Assert.That(parsed.Citations).IsEquivalentTo(["p1.2"]);
        await Assert.That(parsed.Note).IsEqualTo("n");
    }

    [Test]
    public async Task Fifty_one_entries_duplicates_included_drop_the_array() =>
        await Assert.That(Parse("[" + string.Join(",", Enumerable.Repeat(Entry(), 51)) + "]")).IsNull();

    [Test]
    [Arguments("title")]
    [Arguments("blank")]
    [Arguments("origin")]
    [Arguments("status")]
    [Arguments("token")]
    [Arguments("empty-token")]
    [Arguments("citations")]
    [Arguments("note")]
    [Arguments("member")]
    [Arguments("repeated")]
    public async Task Any_entry_over_a_cap_or_outside_the_vocabulary_drops_the_array(string breach) {
        var bad = breach switch {
            "title"       => Entry(title: new string('t', 257)),
            "blank"       => Entry(title: "   "),
            "origin"      => Entry(origin: "wish"),
            "status"      => Entry(status: "done"),
            "token"       => Entry(anchor: new string('a', 17)),
            "empty-token" => Entry(anchor: ""),
            "citations"   => Entry(citations: "[\"a\",\"b\",\"c\",\"d\",\"e\"]"),
            "member"      => Entry()[..^1] + ",\"id\":\"ob:1\"}",
            "repeated"    => Entry()[..^1] + ",\"title\":\"u\"}",
            _             => Entry(note: new string('n', 257))
        };

        await Assert.That(Parse("[" + Entry() + "," + bad + "]")).IsNull();
    }

    [Test]
    public async Task A_non_array_or_malformed_entry_drops_the_array() {
        await Assert.That(Parse("{}")).IsNull();
        await Assert.That(Parse("[1]")).IsNull();
        await Assert.That(Parse("""[{"title":"t"}]""")).IsNull();
    }

    [Test]
    public async Task Every_server_parse_vector_holds() {
        using var vectors = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "eval-strategies", "parse.json")));

        foreach (var v in vectors.RootElement.EnumerateArray()) {
            var parsed = EvalObligationContract.TryParse(v.GetProperty("input"));
            await Assert.That(parsed is not null).IsEqualTo(v.GetProperty("accepted").GetBoolean()).Because(v.GetProperty("name").GetString()!);
            if (parsed is not null) await Assert.That(parsed.Count).IsEqualTo(v.GetProperty("count").GetInt32());
        }
    }

    [Test]
    public async Task The_marker_the_contract_text_and_the_schema_stay_within_their_bounds() {
        await Assert.That(Encoding.UTF8.GetByteCount(EvalObligationContract.ReporterMarker)).IsLessThan(256);
        await Assert.That(EvalObligationContract.ReporterMarker.Contains('\n')).IsFalse();
        await Assert.That(Encoding.UTF8.GetByteCount(EvalObligationContract.ResponseSchema)).IsLessThan(2_048);

        using var schema = JsonDocument.Parse(EvalObligationContract.ObligationsJsonSchema);
        await Assert.That(schema.RootElement.GetProperty("maxItems").GetInt32()).IsEqualTo(EvalObligationContract.MaxObligations);
        await Assert.That(schema.RootElement.GetProperty("description").GetString()).IsEqualTo(EvalObligationContract.ResponseSchema);
        var items = schema.RootElement.GetProperty("items");
        await Assert.That(items.GetProperty("properties").GetProperty("citations").GetProperty("maxItems").GetInt32()).IsEqualTo(EvalObligationContract.MaxCitations);
        await Assert.That(items.GetProperty("properties").GetProperty("origin").GetProperty("enum").EnumerateArray().Select(e => e.GetString()!))
            .IsEquivalentTo(EvalObligationContract.Origins);
        await Assert.That(items.GetProperty("properties").GetProperty("status").GetProperty("enum").EnumerateArray().Select(e => e.GetString()!))
            .IsEquivalentTo(EvalObligationContract.Statuses);
    }
}
