using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class McpSessionsServerTests {
    const string CwdHash = "da9c523c68aee2f1";

    [Test]
    public async Task BuildRepoSessionsUrl_no_repo_uses_cwd_hash_and_defaults_state_to_active() {
        var url = McpSessionsServer.BuildRepoSessionsUrl("http://srv", args: null, cwdRepoHash: CwdHash);

        await Assert.That(url).IsEqualTo($"http://srv/api/repositories/{CwdHash}/sessions?state=active");
    }

    [Test]
    public async Task BuildRepoSessionsUrl_blank_repo_is_treated_as_absent() {
        var url = McpSessionsServer.BuildRepoSessionsUrl("http://srv", new JsonObject { ["repo"] = "  " }, CwdHash);

        await Assert.That(url).Contains($"/api/repositories/{CwdHash}/sessions");
    }

    [Test]
    public async Task BuildRepoSessionsUrl_no_repo_and_no_cwd_hash_fails_closed_without_offering_all() {
        var ex = await Assert.That(() => McpSessionsServer.BuildRepoSessionsUrl("http://srv", args: null, cwdRepoHash: null))
            .Throws<ArgumentException>();

        await Assert.That(ex!.Message).Contains("<owner>/<name>");
        await Assert.That(ex.Message).DoesNotContain("\"all\"");
    }

    [Test]
    public async Task BuildRepoSessionsUrl_owner_name_is_hashed_locally_and_hash_passes_through() {
        var byName = McpSessionsServer.BuildRepoSessionsUrl("http://srv", new JsonObject { ["repo"] = "kurrent-io/kcap-server" }, null);
        var byHash = McpSessionsServer.BuildRepoSessionsUrl("http://srv", new JsonObject { ["repo"] = CwdHash }, null);

        await Assert.That(byName).Contains($"/api/repositories/{RepoHashHelper.ComputeRepoHash("kurrent-io", "kcap-server")}/sessions");
        await Assert.That(byHash).Contains($"/api/repositories/{CwdHash}/sessions");
    }

    [Test]
    [Arguments("all")]
    [Arguments("owner")]
    [Arguments("a//b")]
    [Arguments("DA9C523C68AEE2F1")]
    [Arguments("da9c523c")]
    public async Task BuildRepoSessionsUrl_rejects_malformed_repo(string repo) {
        await Assert.That(() => McpSessionsServer.BuildRepoSessionsUrl("http://srv", new JsonObject { ["repo"] = repo }, CwdHash))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task BuildRepoSessionsUrl_nested_group_owner_resolves_repo_hash() {
        var url = McpSessionsServer.BuildRepoSessionsUrl(
            "http://srv", new JsonObject { ["repo"] = "group/subgroup/project" }, null);

        await Assert.That(url).Contains($"/api/repositories/{RepoHashHelper.ComputeRepoHash("group/subgroup", "project")}/sessions");
    }

    [Test]
    public async Task BuildRepoSessionsUrl_rejects_non_string_repo_and_bad_state() {
        await Assert.That(() => McpSessionsServer.BuildRepoSessionsUrl("http://srv", new JsonObject { ["repo"] = 42 }, CwdHash))
            .Throws<ArgumentException>();
        await Assert.That(() => McpSessionsServer.BuildRepoSessionsUrl("http://srv", new JsonObject { ["state"] = "running" }, CwdHash))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task BuildRepoSessionsUrl_encodes_owner_and_touching_path_and_passes_paging() {
        var url = McpSessionsServer.BuildRepoSessionsUrl(
            "http://srv",
            new JsonObject {
                ["state"]         = "ended",
                ["owner"]         = "user_01ABC DEF",
                ["touching_path"] = "src/Foo Bar.cs",
                ["limit"]         = 5,
                ["offset"]        = 10
            },
            CwdHash);

        await Assert.That(url).Contains("state=ended");
        await Assert.That(url).Contains("owner=user_01ABC%20DEF");
        await Assert.That(url).Contains("touching_path=src%2FFoo%20Bar.cs");
        await Assert.That(url).Contains("limit=5");
        await Assert.That(url).Contains("offset=10");
    }

    [Test]
    public async Task BuildSearchUrl_no_args_no_cwd_hash_fails_closed() {
        // Superseded fail-open expectation: with no repo resolvable and none requested, this now
        // throws instead of silently searching cross-repo. See BuildSearchUrl_fails_closed_when_no_repo_resolves_and_none_requested.
        await Assert.That(() => McpSessionsServer.BuildSearchUrl("http://srv", args: null, cwdRepoHash: null))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task BuildSearchUrl_with_query_only() {
        var url = McpSessionsServer.BuildSearchUrl(
            "http://srv",
            new JsonObject { ["query"] = "batch" },
            cwdRepoHash: "abc1234567890def"
        );

        await Assert.That(url).IsEqualTo("http://srv/api/sessions/search?q=batch&repo=abc1234567890def");
    }

    [Test]
    public async Task BuildSearchUrl_with_query_author_limit() {
        var url = McpSessionsServer.BuildSearchUrl(
            "http://srv",
            new JsonObject {
                ["query"]  = "retry logic",
                ["author"] = "alice",
                ["limit"]  = 25
            },
            cwdRepoHash: "abc1234567890def"
        );

        await Assert.That(url).Contains("q=retry%20logic");
        await Assert.That(url).Contains("author=alice");
        await Assert.That(url).Contains("limit=25");
    }

    [Test]
    public async Task BuildSearchUrl_repo_all_omits_repo_param() {
        var url = McpSessionsServer.BuildSearchUrl(
            "http://srv",
            new JsonObject { ["repo"] = "all" },
            cwdRepoHash: "abc1234567890def"
        );

        await Assert.That(url).IsEqualTo("http://srv/api/sessions/search");
    }

    [Test]
    public async Task BuildSearchUrl_no_repo_uses_cwd_hash() {
        var url = McpSessionsServer.BuildSearchUrl(
            "http://srv",
            args: null,
            cwdRepoHash: "abc1234567890def"
        );

        await Assert.That(url).IsEqualTo("http://srv/api/sessions/search?repo=abc1234567890def");
    }

    [Test]
    public async Task BuildSearchUrl_falls_back_to_cwd_hash_when_repo_is_empty_string() {
        var url = McpSessionsServer.BuildSearchUrl(
            "http://srv",
            new JsonObject { ["repo"] = "" },
            cwdRepoHash: "abc1234567890def"
        );

        await Assert.That(url).IsEqualTo("http://srv/api/sessions/search?repo=abc1234567890def");
    }

    [Test]
    public async Task BuildSearchUrl_falls_back_to_cwd_hash_when_repo_is_whitespace() {
        var url = McpSessionsServer.BuildSearchUrl(
            "http://srv",
            new JsonObject { ["repo"] = "   " },
            cwdRepoHash: "abc1234567890def"
        );

        await Assert.That(url).IsEqualTo("http://srv/api/sessions/search?repo=abc1234567890def");
    }

    [Test]
    public async Task BuildSearchUrl_explicit_repo_overrides_cwd_hash() {
        var url = McpSessionsServer.BuildSearchUrl(
            "http://srv",
            new JsonObject { ["repo"] = "kurrent-io/kcap" },
            cwdRepoHash: "abc1234567890def"
        );

        await Assert.That(url).IsEqualTo("http://srv/api/sessions/search?repo=kurrent-io%2Fkcap");
    }

    [Test]
    public async Task BuildSearchUrl_author_github_id_takes_precedence_via_query_param() {
        var url = McpSessionsServer.BuildSearchUrl(
            "http://srv",
            new JsonObject { ["author_github_id"] = 12345 },
            cwdRepoHash: "abc1234567890def"
        );

        await Assert.That(url).IsEqualTo("http://srv/api/sessions/search?author_github_id=12345&repo=abc1234567890def");
    }

    [Test]
    public async Task ProjectRecapToSummary_empty_array() {
        var projected = McpSessionsServer.ProjectRecapToSummary("[]");

        using var doc = JsonDocument.Parse(projected);
        await Assert.That(doc.RootElement.GetProperty("summary_text").GetString()).IsEqualTo("");
        await Assert.That(doc.RootElement.GetProperty("plan").ValueKind).IsEqualTo(JsonValueKind.Null);
    }

    [Test]
    public async Task ProjectRecapToSummary_one_whats_done_one_plan() {
        const string body = """
            [
              {"type": "whats_done", "content": "Implemented feature X"},
              {"type": "plan",       "content": "Step 1: do thing"}
            ]
            """;

        var projected = McpSessionsServer.ProjectRecapToSummary(body);

        using var doc = JsonDocument.Parse(projected);
        await Assert.That(doc.RootElement.GetProperty("summary_text").GetString()).IsEqualTo("Implemented feature X");
        await Assert.That(doc.RootElement.GetProperty("plan").GetString()).IsEqualTo("Step 1: do thing");
    }

    [Test]
    public async Task ProjectRecapToSummary_multiple_whats_done_latest_wins() {
        const string body = """
            [
              {"type": "whats_done", "content": "First pass"},
              {"type": "whats_done", "content": "Second pass"},
              {"type": "whats_done", "content": "Final"}
            ]
            """;

        var projected = McpSessionsServer.ProjectRecapToSummary(body);

        using var doc = JsonDocument.Parse(projected);
        await Assert.That(doc.RootElement.GetProperty("summary_text").GetString()).IsEqualTo("Final");
        await Assert.That(doc.RootElement.GetProperty("plan").ValueKind).IsEqualTo(JsonValueKind.Null);
    }

    [Test]
    public async Task ProjectRecapToSummary_escapes_special_chars() {
        const string body = """
            [
              {"type": "whats_done", "content": "Has \"quotes\" and \nnewlines"}
            ]
            """;

        var projected = McpSessionsServer.ProjectRecapToSummary(body);

        // Must be parseable as JSON
        using var doc = JsonDocument.Parse(projected);
        await Assert.That(doc.RootElement.GetProperty("summary_text").GetString()).IsEqualTo("Has \"quotes\" and \nnewlines");
    }

    [Test]
    public async Task BuildTranscriptUrl_session_id_only_emits_minimal_url() {
        var url = InvokeBuildTranscriptUrl(
            "http://srv",
            new JsonObject { ["session_id"] = "abc-123" }
        );

        await Assert.That(url).IsEqualTo("http://srv/api/sessions/abc-123/transcript");
    }

    [Test]
    public async Task BuildTranscriptUrl_includes_around_event_and_agent_id_when_set() {
        var url = InvokeBuildTranscriptUrl(
            "http://srv",
            new JsonObject {
                ["session_id"]   = "abc-123",
                ["around_event"] = 42,
                ["agent_id"]     = "sub-7"
            }
        );

        await Assert.That(url).Contains("around_event=42");
        await Assert.That(url).Contains("agent_id=sub-7");
    }

    [Test]
    public async Task BuildTranscriptUrl_emits_chain_as_lowercase_bool() {
        var url = InvokeBuildTranscriptUrl(
            "http://srv",
            new JsonObject {
                ["session_id"] = "abc-123",
                ["chain"]      = true
            }
        );

        await Assert.That(url).Contains("chain=true");
        await Assert.That(url).DoesNotContain("chain=True");
    }

    [Test]
    public async Task BuildTranscriptUrl_throws_when_session_id_missing() {
        var ex = Assert.Throws<ArgumentException>(() => InvokeBuildTranscriptUrl("http://srv", new JsonObject()));

        await Assert.That(ex!.Message).Contains("session_id");
    }

    [Test]
    public async Task BuildTranscriptUrl_emits_include_thinking_false_when_explicitly_set() {
        var url = InvokeBuildTranscriptUrl(
            "http://srv",
            new JsonObject {
                ["session_id"]       = "abc-123",
                ["include_thinking"] = false
            }
        );

        // Documents current behaviour: helper appends the param whenever it's present in args,
        // even when its value matches the server-side default of false.
        await Assert.That(url).Contains("include_thinking=false");
    }

    [Test]
    public async Task BuildSearchUrl_accepts_author_github_id_above_int_max() {
        var url = McpSessionsServer.BuildSearchUrl(
            "http://srv",
            new JsonObject { ["author_github_id"] = 5_000_000_000L },
            cwdRepoHash: "abc1234567890def"
        );

        await Assert.That(url).Contains("author_github_id=5000000000");
    }

    [Test]
    public async Task TryReadInt_throws_on_long_overflow() {
        var args = new JsonObject { ["limit"] = 5_000_000_000L };

        Assert.Throws<ArgumentException>(() => McpSessionsServer.TryReadInt(args, "limit", out _));
        await Task.CompletedTask;
    }

    [Test]
    public async Task TryReadInt_returns_false_for_missing_key() {
        var ok = McpSessionsServer.TryReadInt(new JsonObject(), "limit", out var value);

        await Assert.That(ok).IsFalse();
        await Assert.That(value).IsEqualTo(0);
    }

    [Test]
    public async Task TryReadInt_returns_false_for_wrong_type() {
        var args = new JsonObject { ["limit"] = "not-a-number" };

        var ok = McpSessionsServer.TryReadInt(args, "limit", out _);

        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task BuildSearchUrl_fails_closed_when_no_repo_resolves_and_none_requested() {
        var args = new JsonObject { ["query"] = "hi" }; // no repo arg, cwdRepoHash null
        await Assert.That(() => McpSessionsServer.BuildSearchUrl("http://x", args, cwdRepoHash: null))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task BuildSearchUrl_rejects_nonstring_repo_with_clean_error() {
        var args = new JsonObject { ["query"] = "hi", ["repo"] = 123 };
        await Assert.That(() => McpSessionsServer.BuildSearchUrl("http://x", args, cwdRepoHash: "abc"))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task BuildSearchUrl_allows_explicit_cross_repo_all() {
        var url = McpSessionsServer.BuildSearchUrl("http://x", new JsonObject { ["query"] = "hi", ["repo"] = "all" }, cwdRepoHash: null);
        await Assert.That(url).DoesNotContain("repo="); // cross-repo → repo param omitted, no throw
    }

    [Test]
    public async Task BuildSearchUrl_uses_explicit_repo_when_cwd_absent() {
        var url = McpSessionsServer.BuildSearchUrl("http://x", new JsonObject { ["query"] = "hi", ["repo"] = "owner/name" }, cwdRepoHash: null);
        await Assert.That(url).Contains("repo=owner%2Fname");
    }

    [Test]
    public async Task BuildSearchUrl_uses_cwd_repo_when_no_explicit() {
        var url = McpSessionsServer.BuildSearchUrl("http://x", new JsonObject { ["query"] = "hi" }, cwdRepoHash: "abc123");
        await Assert.That(url).Contains("repo=abc123");
    }

    [Test]
    public async Task BuildTurnsUrl_builds_session_turns_url() {
        var url = McpSessionsServer.BuildTurnsUrl(
            "http://srv",
            new JsonObject { ["session_id"] = "abc-123" }
        );

        await Assert.That(url).IsEqualTo("http://srv/api/sessions/abc-123/turns");
    }

    [Test]
    public async Task BuildTurnsUrl_escapes_session_id() {
        var url = McpSessionsServer.BuildTurnsUrl(
            "http://srv",
            new JsonObject { ["session_id"] = "a/b c" }
        );

        await Assert.That(url).IsEqualTo("http://srv/api/sessions/a%2Fb%20c/turns");
    }

    [Test]
    public async Task BuildTurnsUrl_throws_when_session_id_missing() {
        var ex = Assert.Throws<ArgumentException>(() => McpSessionsServer.BuildTurnsUrl("http://srv", new JsonObject()));

        await Assert.That(ex!.Message).Contains("session_id");
    }

    static string Body(params string[] sessionIds) {
        var hits = string.Join(",", sessionIds.Select(id => $"{{\"session_id\":\"{id}\",\"title\":\"t\"}}"));
        return $"{{\"hits\":[{hits}]}}";
    }

    [Test]
    public async Task ShouldWiden_true_when_cwd_pinned_and_thin() {
        var widen = McpSessionsServer.ShouldWiden(
            new JsonObject { ["query"] = "x" }, cwdRepoHash: "abc1234567890def",
            firstBody: Body("s1", "s2"), out var limit);

        await Assert.That(widen).IsTrue();
        await Assert.That(limit).IsEqualTo(10);
    }

    [Test]
    public async Task ShouldWiden_false_when_repo_explicit() {
        var widen = McpSessionsServer.ShouldWiden(
            new JsonObject { ["query"] = "x", ["repo"] = "kurrent-io/kcap-server" },
            cwdRepoHash: "abc1234567890def", firstBody: Body(), out _);

        await Assert.That(widen).IsFalse();
    }

    [Test]
    public async Task ShouldWiden_false_when_repo_all() {
        var widen = McpSessionsServer.ShouldWiden(
            new JsonObject { ["query"] = "x", ["repo"] = "all" },
            cwdRepoHash: "abc1234567890def", firstBody: Body(), out _);

        await Assert.That(widen).IsFalse();
    }

    [Test]
    public async Task ShouldWiden_false_when_results_fill_the_limit() {
        var widen = McpSessionsServer.ShouldWiden(
            new JsonObject { ["query"] = "x", ["limit"] = 2 },
            cwdRepoHash: "abc1234567890def", firstBody: Body("s1", "s2"), out var limit);

        await Assert.That(widen).IsFalse();
        await Assert.That(limit).IsEqualTo(2);
    }

    [Test]
    public async Task ShouldWiden_false_when_paginating() {
        var widen = McpSessionsServer.ShouldWiden(
            new JsonObject { ["query"] = "x", ["offset"] = 10 },
            cwdRepoHash: "abc1234567890def", firstBody: Body(), out _);

        await Assert.That(widen).IsFalse();
    }

    [Test]
    public async Task ShouldWiden_false_on_author_short_circuits() {
        var disamb = "{\"hits\":[],\"disambiguation\":[{\"git_hub_id\":1}]}";
        var noAuth = "{\"hits\":[],\"no_author_match\":true}";

        await Assert.That(McpSessionsServer.ShouldWiden(new JsonObject { ["query"] = "x" }, "abc1234567890def", disamb, out _)).IsFalse();
        await Assert.That(McpSessionsServer.ShouldWiden(new JsonObject { ["query"] = "x" }, "abc1234567890def", noAuth, out _)).IsFalse();
    }

    [Test]
    public async Task ShouldWiden_false_when_no_cwd_hash() {
        var widen = McpSessionsServer.ShouldWiden(new JsonObject { ["query"] = "x" }, cwdRepoHash: null, firstBody: Body(), out _);

        await Assert.That(widen).IsFalse();
    }

    [Test]
    public async Task ShouldWiden_false_when_repo_is_non_string() {
        var widen = McpSessionsServer.ShouldWiden(
            new JsonObject { ["query"] = "x", ["repo"] = 5 },
            cwdRepoHash: "abc1234567890def", firstBody: Body(), out _);

        await Assert.That(widen).IsFalse();
    }

    [Test]
    public async Task MergeWidenedBody_dedupes_keeps_cwd_first_caps_and_flags() {
        var merged = McpSessionsServer.MergeWidenedBody(
            firstBody: Body("s1", "s2"),
            widenedBody: Body("s2", "s3", "s4"),
            limit: 3);

        var root = JsonNode.Parse(merged)!.AsObject();
        var ids  = root["hits"]!.AsArray().Select(h => h!["session_id"]!.GetValue<string>()).ToList();

        await Assert.That(ids.Count).IsEqualTo(3);
        await Assert.That(ids[0]).IsEqualTo("s1");
        await Assert.That(ids[1]).IsEqualTo("s2");
        await Assert.That(ids[2]).IsEqualTo("s3");
        await Assert.That(root["widened_to_all_repos"]!.GetValue<bool>()).IsTrue();
    }

    [Test]
    public async Task MergeWidenedBody_lexical_match_in_widened_scope_clears_no_lexical_match() {
        var merged = McpSessionsServer.MergeWidenedBody(
            firstBody: """{"no_lexical_match":true,"hits":[{"session_id":"s1","lanes":["semantic"]}]}""",
            widenedBody: """{"no_lexical_match":false,"hits":[{"session_id":"s2","lanes":["transcript"]}]}""",
            limit: 10);

        var root = JsonNode.Parse(merged)!.AsObject();

        await Assert.That(root["no_lexical_match"]!.GetValue<bool>()).IsFalse();
        await Assert.That(root["widened_to_all_repos"]!.GetValue<bool>()).IsTrue();
    }

    [Test]
    [Arguments("true", "true", "true")]
    [Arguments("false", "true", "false")]
    [Arguments("null", "true", "true")]
    [Arguments("true", "null", "true")]
    [Arguments("null", "null", "null")]
    [Arguments(null, null, null)]
    public async Task MergeWidenedBody_combines_no_lexical_match_across_scopes(string? first, string? widened, string? expected) {
        static string WithFlag(string? flag, string sid) =>
            flag is null
                ? Body(sid)
                : $$"""{"no_lexical_match":{{flag}},"hits":[{"session_id":"{{sid}}"}]}""";

        var merged = McpSessionsServer.MergeWidenedBody(WithFlag(first, "s1"), WithFlag(widened, "s2"), limit: 10);
        var flag   = JsonNode.Parse(merged)!.AsObject()["no_lexical_match"];

        await Assert.That(flag?.ToJsonString() ?? "null").IsEqualTo(expected ?? "null");
    }

    [Test]
    public async Task MergeWidenedBody_malformed_widened_body_returns_first_unchanged() {
        var first  = Body("s1");
        var merged = McpSessionsServer.MergeWidenedBody(first, "not json", limit: 10);

        await Assert.That(merged).IsEqualTo(first);
    }

    // BuildTranscriptUrl is private; reach it via the public test entry point HandleToolCallForTests
    // would round-trip through HTTP, so instead we use reflection for narrow per-builder coverage.
    static string InvokeBuildTranscriptUrl(string baseUrl, JsonObject args) {
        var method = typeof(McpSessionsServer).GetMethod(
            "BuildTranscriptUrl",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic
        ) ?? throw new InvalidOperationException("BuildTranscriptUrl not found");

        try {
            return (string)method.Invoke(null, [baseUrl, args])!;
        } catch (System.Reflection.TargetInvocationException tie) when (tie.InnerException is not null) {
            throw tie.InnerException;
        }
    }

    [Test]
    public async Task BuildRepoPlansUrl_no_args_uses_cwd_hash_and_defaults_state_to_open() {
        var url = McpSessionsServer.BuildRepoPlansUrl("http://srv", args: null, cwdRepoHash: CwdHash);

        await Assert.That(url).IsEqualTo($"http://srv/api/repositories/{CwdHash}/plans?state=open");
    }

    [Test]
    public async Task BuildRepoPlansUrl_carries_state_owner_and_limit() {
        var args = new JsonObject { ["state"] = "all", ["owner"] = "github:1 2", ["limit"] = 5 };

        var url = McpSessionsServer.BuildRepoPlansUrl("http://srv", args, CwdHash);

        await Assert.That(url).IsEqualTo($"http://srv/api/repositories/{CwdHash}/plans?state=all&owner=github%3A1%202&limit=5");
    }

    [Test]
    public async Task BuildRepoPlansUrl_rejects_an_unknown_state() {
        var ex = await Assert.That(() => McpSessionsServer.BuildRepoPlansUrl("http://srv", new JsonObject { ["state"] = "done" }, CwdHash))
            .Throws<ArgumentException>();

        await Assert.That(ex!.Message).Contains("open or all");
    }

    [Test]
    public async Task BuildRepoPlansUrl_no_repo_and_no_cwd_hash_fails_closed_without_offering_all() {
        var ex = await Assert.That(() => McpSessionsServer.BuildRepoPlansUrl("http://srv", args: null, cwdRepoHash: null))
            .Throws<ArgumentException>();

        await Assert.That(ex!.Message).Contains("<owner>/<name>");
        await Assert.That(ex.Message).DoesNotContain("\"all\"");
    }

    [Test]
    public async Task BuildDeclaredPlansUrl_by_plan_id_reads_one_plan() {
        var url = McpSessionsServer.BuildDeclaredPlansUrl("http://srv", new JsonObject { ["plan_id"] = "p 1" }, out var single);

        await Assert.That(url).IsEqualTo("http://srv/api/plans/p%201");
        await Assert.That(single).IsTrue();
    }

    [Test]
    public async Task BuildDeclaredPlansUrl_by_session_id_reads_the_sessions_plans() {
        var url = McpSessionsServer.BuildDeclaredPlansUrl("http://srv", new JsonObject { ["session_id"] = "s1" }, out var single);

        await Assert.That(url).IsEqualTo("http://srv/api/sessions/s1/plans");
        await Assert.That(single).IsFalse();
    }

    [Test]
    public async Task BuildDeclaredPlansUrl_needs_exactly_one_of_plan_id_and_session_id() {
        var neither = await Assert.That(() => McpSessionsServer.BuildDeclaredPlansUrl("http://srv", new JsonObject(), out _))
            .Throws<ArgumentException>();
        var both = await Assert.That(() => McpSessionsServer.BuildDeclaredPlansUrl("http://srv", new JsonObject { ["plan_id"] = "p1", ["session_id"] = "s1" }, out _))
            .Throws<ArgumentException>();

        await Assert.That(neither!.Message).Contains("exactly one");
        await Assert.That(both!.Message).Contains("exactly one");
    }

    /// <summary>"current" names a session's pointer and needs a session the route would not get;
    /// a dot segment would walk the URL path.</summary>
    [Test]
    [Arguments("current")]
    [Arguments(".")]
    [Arguments("..")]
    public async Task BuildDeclaredPlansUrl_rejects_a_plan_id_that_is_not_an_id(string planId) {
        var ex = await Assert.That(() => McpSessionsServer.BuildDeclaredPlansUrl("http://srv", new JsonObject { ["plan_id"] = planId }, out _))
            .Throws<ArgumentException>();

        await Assert.That(ex!.Message).Contains("session_id");
    }

    /// <summary>A dot segment in session_id would walk the URL path the same way it does for plan_id.</summary>
    [Test]
    [Arguments(".")]
    [Arguments("..")]
    public async Task BuildDeclaredPlansUrl_rejects_a_dot_segment_session_id(string sessionId) {
        var ex = await Assert.That(() => McpSessionsServer.BuildDeclaredPlansUrl("http://srv", new JsonObject { ["session_id"] = sessionId }, out _))
            .Throws<ArgumentException>();

        await Assert.That(ex!.Message).Contains("not \".\" or \"..\"");
    }

    [Test]
    public async Task Tools_list_exposes_the_two_plan_tools_with_no_required_arguments() {
        var byName = McpSessionsServer.BuildToolsList().ToDictionary(t => t.Name);

        await Assert.That(byName["list_repo_plans"].InputSchema.Required.Length).IsEqualTo(0);
        await Assert.That(byName["get_declared_plans"].InputSchema.Required.Length).IsEqualTo(0);
        await Assert.That(byName["get_declared_plans"].Description).Contains("is_complete");
        await Assert.That(byName["list_repo_plans"].Description).Contains("finished");
    }

    const string Recap = """[{"type":"whats_done","content":"did X"}]""";

    static JsonArray? DeclaredPlans(string? plansBody) =>
        JsonNode.Parse(McpSessionsServer.ProjectRecapToSummary(Recap, plansBody))!["declared_plans"]?.AsArray();

    [Test]
    public async Task ProjectRecapToSummary_carries_a_pointer_for_each_declared_plan() {
        const string plans = """
            [
              {"plan_id":"p-1","progress":{"completed":2,"total":7,"total_known":true,"finished":false},"is_complete":true,"is_current":true,"tasks":[{"title":"ignored"}]},
              {"plan_id":"p-2","progress":{"completed":3,"total":3,"total_known":true,"finished":true},"is_complete":true,"is_current":false}
            ]
            """;

        var pointers = DeclaredPlans(plans)!;

        await Assert.That(pointers.Count).IsEqualTo(2);
        await Assert.That(pointers[0]!.ToJsonString())
            .IsEqualTo("""{"plan_id":"p-1","completed":2,"total":7,"total_known":true,"finished":false,"is_complete":true,"is_current":true}""");
        await Assert.That(pointers[1]!["finished"]!.GetValue<bool>()).IsTrue();
    }

    [Test]
    [Arguments(null)]
    [Arguments("[]")]
    [Arguments("not json")]
    [Arguments("""{"error":"nope"}""")]
    public async Task ProjectRecapToSummary_omits_declared_plans_when_there_is_nothing_to_show(string? plansBody) {
        var projected = JsonNode.Parse(McpSessionsServer.ProjectRecapToSummary(Recap, plansBody))!.AsObject();

        await Assert.That(projected.ContainsKey("declared_plans")).IsFalse();
        await Assert.That(projected["summary_text"]!.GetValue<string>()).IsEqualTo("did X");
    }

    /// <summary>A server that predates the field omits it. Both zero-task shapes read 0 of 0 and
    /// differ only in total_known, so completed == total alone would call a plan with no task list
    /// finished.</summary>
    [Test]
    [Arguments("""{"completed":0,"total":0,"total_known":false}""", true,  false)]
    [Arguments("""{"completed":0,"total":0,"total_known":true}""",  true,  true)]
    [Arguments("""{"completed":3,"total":3,"total_known":true}""",  false, false)]
    [Arguments("""{"completed":3,"total":3,"total_known":true}""",  true,  true)]
    [Arguments("""{"completed":2,"total":3,"total_known":true}""",  true,  false)]
    public async Task ProjectDeclaredPlans_derives_finished_when_the_server_did_not_send_it(string progress, bool isComplete, bool expected) {
        var plans = $$"""[{"plan_id":"p-1","progress":{{progress}},"is_complete":{{(isComplete ? "true" : "false")}},"is_current":false}]""";

        await Assert.That(DeclaredPlans(plans)![0]!["finished"]!.GetValue<bool>()).IsEqualTo(expected);
    }

    [Test]
    public async Task ProjectDeclaredPlans_trusts_a_finished_the_server_sent() {
        const string plans = """[{"plan_id":"p-1","progress":{"completed":3,"total":3,"total_known":true,"finished":false},"is_complete":true,"is_current":false}]""";

        await Assert.That(DeclaredPlans(plans)![0]!["finished"]!.GetValue<bool>()).IsFalse();
    }

    [Test]
    public async Task ProjectDeclaredPlans_skips_an_entry_with_no_plan_id() {
        const string plans = """[{"progress":{"completed":0,"total":1,"total_known":true}},{"plan_id":"p-2","progress":{"completed":0,"total":1,"total_known":true},"is_complete":true,"is_current":false}]""";

        var pointers = DeclaredPlans(plans)!;

        await Assert.That(pointers.Count).IsEqualTo(1);
        await Assert.That(pointers[0]!["plan_id"]!.GetValue<string>()).IsEqualTo("p-2");
    }
}
