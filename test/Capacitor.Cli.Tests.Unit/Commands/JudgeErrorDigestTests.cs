using System.Text.Json;
using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class JudgeErrorDigestTests {
    static string Entry(string session, int number, string tool, string input, string error, string? agent = null, string? callId = null) =>
        $$"""
        {"session_id":"{{session}}","event_number":{{number}},"tool_name":"{{tool}}","tool_input":{{input}},"error":{{JsonSerializer.Serialize(error)}}
        {{(agent is null ? "" : $",\"agent_id\":\"{agent}\"")}}{{(callId is null ? "" : $",\"call_id\":\"{callId}\"")}}}
        """;

    static JsonElement Render(int offset, int limit, params string[] entries) =>
        JsonDocument.Parse(JudgeErrorDigest.Render("[" + string.Join(",", entries) + "]", "aaaaaaaa1111", offset, limit)).RootElement.Clone();

    static string[] Refs(JsonElement array) => array.EnumerateArray().Select(r => r.GetProperty("ref").GetString()!).ToArray();

    [Test]
    public async Task Failures_sharing_a_tool_and_operation_are_grouped_and_a_single_failure_is_not() {
        var root = Render(0, 40,
            Entry("aaaaaaaa1111", 3, "Bash", """{"command":"dotnet   build"}""", "error CS1002"),
            Entry("aaaaaaaa1111", 7, "Bash", """{"command":"dotnet build","timeout":60000}""", "error CS1002"),
            Entry("aaaaaaaa1111", 8, "Edit", """{"file_path":"a.cs","old_string":"x"}""", "not found"),
            Entry("aaaaaaaa1111", 9, "Bash", """{"command":"dotnet test"}""", "failed"));

        var groups = root.GetProperty("repeated_operations");
        await Assert.That(groups.GetArrayLength()).IsEqualTo(1);
        await Assert.That(groups[0].GetProperty("operation").GetString()).IsEqualTo("dotnet build");
        await Assert.That(groups[0].GetProperty("failures").GetInt32()).IsEqualTo(2);
        await Assert.That(string.Join(",", groups[0].GetProperty("refs").EnumerateArray().Select(r => r.GetString()))).IsEqualTo("#3,#7");
        await Assert.That(root.GetProperty("distinct_operations").GetInt32()).IsEqualTo(3);
        await Assert.That(root.GetProperty("failures")[2].GetProperty("operation").GetString()).IsEqualTo("a.cs");
    }

    [Test]
    public async Task Failures_naming_no_operation_are_never_grouped() {
        var root = Render(0, 40,
            Entry("aaaaaaaa1111", 1, "Bash", "null", "e"),
            Entry("aaaaaaaa1111", 2, "Bash", "{}", "e"));

        await Assert.That(root.GetProperty("repeated_operations").GetArrayLength()).IsEqualTo(0);
        await Assert.That(root.GetProperty("distinct_operations").GetInt32()).IsEqualTo(2);
    }

    [Test]
    public async Task Operations_sharing_a_long_prefix_are_grouped_by_their_whole_text() {
        var prefix = new string('p', 300);

        var root = Render(0, 40,
            Entry("aaaaaaaa1111", 1, "Bash", $$"""{"command":"{{prefix}} one"}""", "e"),
            Entry("aaaaaaaa1111", 2, "Bash", $$"""{"command":"{{prefix}} two"}""", "e"));

        await Assert.That(root.GetProperty("repeated_operations").GetArrayLength()).IsEqualTo(0);
        await Assert.That(root.GetProperty("failures")[0].GetProperty("operation").GetString()!.Length).IsEqualTo(201);
    }

    [Test]
    public async Task A_full_page_stays_one_document_within_the_cap_and_resumes_after_its_last_row() {
        var entries = Enumerable.Range(0, 300)
            .Select(i => Entry("aaaaaaaa1111", i, "Bash", $$"""{"command":"{{new string('c', 400)}} {{i % 150}}"}""", new string('e', 2_000)))
            .ToArray();
        var body = "[" + string.Join(",", entries) + "]";

        var text = JudgeErrorDigest.Render(body, "aaaaaaaa1111", 0, JudgeErrorDigest.MaxLimit);
        using var doc = JsonDocument.Parse(text);
        var rows = doc.RootElement.GetProperty("failures").GetArrayLength();

        await Assert.That(text.Length).IsLessThanOrEqualTo(JudgeResultCap.MaxChars);
        await Assert.That(rows).IsLessThan(JudgeErrorDigest.MaxLimit);
        await Assert.That(doc.RootElement.GetProperty("next_offset").GetInt32()).IsEqualTo(rows);
        await Assert.That(doc.RootElement.GetProperty("repeated_operations_omitted").GetInt32()).IsGreaterThan(0);
    }

    [Test]
    public async Task A_page_names_the_next_offset_only_while_rows_remain() {
        var entries = Enumerable.Range(0, 5).Select(i => Entry("aaaaaaaa1111", i, "Bash", $$"""{"command":"c{{i}}"}""", "e")).ToArray();

        var first = Render(0, 2, entries);
        var last  = Render(4, 2, entries);

        await Assert.That(first.GetProperty("failures").GetArrayLength()).IsEqualTo(2);
        await Assert.That(first.GetProperty("next_offset").GetInt32()).IsEqualTo(2);
        await Assert.That(last.GetProperty("failures").GetArrayLength()).IsEqualTo(1);
        await Assert.That(last.TryGetProperty("next_offset", out _)).IsFalse();
        await Assert.That(last.GetProperty("total_failures").GetInt32()).IsEqualTo(5);
    }

    [Test]
    public async Task A_long_error_keeps_its_head_and_tail() {
        var error = "HEAD " + new string('x', 5_000) + " TAIL";

        var row = Render(0, 40, Entry("aaaaaaaa1111", 1, "Bash", """{"command":"make"}""", error)).GetProperty("failures")[0].GetProperty("error").GetString()!;

        await Assert.That(row).StartsWith("HEAD ");
        await Assert.That(row).EndsWith($" TAIL ({error.Length} chars)");
        await Assert.That(row.Length).IsLessThan(400);
    }

    [Test]
    public async Task A_ref_names_the_agent_and_names_the_session_only_across_a_chain() {
        var single = Render(0, 40, Entry("aaaaaaaa1111", 2, "Bash", "{}", "e", agent: "ag1", callId: "toolu_1"));
        var chain  = Render(0, 40,
            Entry("aaaaaaaa1111", 2, "Bash", "{}", "e"),
            Entry("bbbbbbbb2222", 5, "Bash", "{}", "e", agent: "ag1"));

        await Assert.That(single.GetProperty("failures")[0].GetProperty("ref").GetString()).IsEqualTo("ag1:#2");
        await Assert.That(single.GetProperty("failures")[0].GetProperty("call_id").GetString()).IsEqualTo("toolu_1");
        await Assert.That(single.GetProperty("failures")[0].GetProperty("agent_id").GetString()).IsEqualTo("ag1");
        await Assert.That(string.Join(",", Refs(chain.GetProperty("failures")))).IsEqualTo("aaaaaaaa/#2,bbbbbbbb/ag1:#5");
    }

    [Test]
    public async Task Only_the_bound_sessions_rows_carry_a_call_id() {
        var root = Render(0, 40,
            Entry("aaaaaaaa1111", 2, "Bash", "{}", "e", callId: "toolu_root"),
            Entry("bbbbbbbb2222", 5, "Bash", "{}", "e", callId: "toolu_next"));

        var failures = root.GetProperty("failures");
        await Assert.That(failures[0].GetProperty("call_id").GetString()).IsEqualTo("toolu_root");
        await Assert.That(failures[1].TryGetProperty("call_id", out _)).IsFalse();
    }
}
