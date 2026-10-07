# Session Artefacts in the Desktop App Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Render every kcap MCP tool call in the chat in the product's own words, turn the producing calls into cards, and add an Artefacts tab that lists and renders a session's declared documents.

**Architecture:** A data catalogue keyed by tool-name suffix drives the chat row text and marks the calls that become cards; a card is a `ToolCallItem` with a `ToolCard` attached when its result settles. The Artefacts tab is one view model fed by a totalized read of `plan-artifacts?chain=true`, with a master list and a `DocumentReader` over the existing `MarkdownView`; the pane's PLAN rows and the chat cards open into it.

**Tech Stack:** .NET 10, Avalonia 12.1.3, ReactiveUI, MarkView.Avalonia, TUnit on Microsoft Testing Platform, Avalonia.Headless for view smoke tests.

**Spec:** `docs/superpowers/specs/2026-10-07-session-artefacts-desktop-design.md`. This plan covers the spec's delivery slices 1 and 2 (tool rows and cards; the Artefacts tab with documents). Slices 3 and 4 (pages, the embedded engine) need a kcap-server route and a new native dependency and get their own plans.

## Global Constraints

- The feature is spelled **Artefacts**, with an *e*, in code, XAML names, copy and docs. `PlanArtifact*` with an *i* is the existing plan-discovery vocabulary and stays as it is.
- One type per file, named after the type. An enum plus its extension methods may share a file.
- No new package references in this plan.
- Comments are scarce; no change narration, no issue ids as narration, no review artefacts. Test doc comments say what the test pins.
- Read JSON through `JsonElementExtensions` (`Str`, `Arr`, `IsObject`), never by checking `ValueKind`.
- Colour rules: success green and warning orange mean outcome or attention only; the open row uses `KcapPurple*` (location); selection uses `KcapSelectionBrush`.
- Tool-name matching is by suffix: Claude prefixes the server (`mcp__plugin_kcap_kcap-plans__declare_plan_document`), Codex records the bare name (`declare_plan_document`).
- Every UI-touching test runs under `RunOnUiAsync` and carries `[NotInParallel("AvaloniaSession")]`; pure tests carry neither.
- Commit subjects: one imperative clause, at most 80 characters including `(#1098)`. Every commit ends with `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`.
- `README.md` must change in the same PR as the user-facing change (Task 11).
- Rows take their detail from the call's arguments. Result-derived counts in rows ("10 hits", "1 of 3 so far") are not in this plan.
- Flow and hosted-agent cards are built from arguments and carry no Open action: their result shapes are not pinned in this repo. Page and document cards carry Open.

## Review Focus

- A `publish_artefact` result that is an error string, or JSON cut at the 4096-character cap: the row stays a plain row, nothing throws. Test in Task 3 (`ToolCardsTests`).
- A declared path with backslashes or an absolute prefix: the file name cuts on either separator, and a PLAN row or card still finds its document by path suffix. Tests in Task 8 (`ArtefactsTabViewModelTests`).
- A plan-artifacts entry with `content_state: "unavailable"` and null `content`: the reader shows the header and the notice and no body, with no null dereference. Test in Task 8 (`DocumentReaderViewModelTests`).
- The working copy deleted after declaration: the drift notice says the file is gone instead of throwing. Test in Task 8.
- A session switch while a read is in flight: the stale read never applies, and when the new session has no documents the tab hides and the active tab falls back to Chat. Tests in Tasks 8 and 9.

---

## Slice 1: tool rows and cards

### Task 1: The kcap tool catalogue

**Files:**
- Create: `src/Capacitor.App/ViewModels/ToolCardKind.cs`
- Create: `src/Capacitor.App/ViewModels/KcapToolEntry.cs`
- Create: `src/Capacitor.App/ViewModels/KcapToolCatalogue.cs`
- Modify: `src/Capacitor.App/ViewModels/ToolSummary.cs:7` (the `ToolCategory` enum gains members and their phrases)
- Test: `test/Capacitor.App.Tests.Unit/KcapToolCatalogueTests.cs`

**Interfaces:**
- Produces: `enum ToolCardKind { None, Page, Document, Flow, Agent }`; `record KcapToolEntry(string Tool, ToolCategory Category, string Label, string? DetailKey, ToolCardKind Card = ToolCardKind.None)`; `static KcapToolEntry? KcapToolCatalogue.Match(string? toolName)`; `static string? KcapToolCatalogue.ForeignLabel(string? toolName)`; `ToolCategory.Artefact`, `.Work`, `.Memory`, `.Session`, `.Flow`.

- [ ] **Step 1: Add the new categories so the test compiles**

In `src/Capacitor.App/ViewModels/ToolSummary.cs` replace line 7:

```csharp
public enum ToolCategory { Read, Edit, Command, Search, WebSearch, Fetch, Skill, Agent, Plan, Question, Artefact, Work, Memory, Session, Flow, Other }
```

`Phrases` is indexed by the enum, so until Task 2 adds the five phrases `Describe` would read past the array for a new member. Append five placeholder tuples now, in this order, directly after `("Asked a question", "Asked questions"),`:

```csharp
        ("Worked on a page", "Worked on pages"),
        ("Tracked work", "Tracked work"),
        ("Used team memory", "Used team memory"),
        ("Recalled sessions", "Recalled sessions"),
        ("Ran a flow", "Ran flows"),
```

and keep `("Called a tool", "Called tools")` last.

- [ ] **Step 2: Write the failing tests**

Create `test/Capacitor.App.Tests.Unit/KcapToolCatalogueTests.cs`:

```csharp
using Capacitor.App.ViewModels;

namespace Capacitor.App.Tests.Unit;

/// Pure: no dispatcher, so no session constraint.
public class KcapToolCatalogueTests {
    [Test]
    [Arguments("mcp__plugin_kcap_kcap-plans__declare_plan_document")]
    [Arguments("mcp__kcap-plans__declare_plan_document")]
    [Arguments("declare_plan_document")]
    public async Task Claude_and_codex_spellings_of_one_tool_match_the_same_entry(string name) {
        var entry = KcapToolCatalogue.Match(name);
        await Assert.That(entry).IsNotNull();
        await Assert.That(entry!.Tool).IsEqualTo("declare_plan_document");
        await Assert.That(entry.Category).IsEqualTo(ToolCategory.Plan);
        await Assert.That(entry.Card).IsEqualTo(ToolCardKind.Document);
    }

    /// A suffix must be the whole bare name: `set_plan_tasks` is not `plan_tasks`, and a foreign
    /// server's `save_memory` is not ours when its server segment says otherwise.
    [Test]
    [Arguments("mcp__other__save_memory")]
    [Arguments("mcp__plugin_acme_acme__publish_artefact")]
    [Arguments("xdeclare_plan_document")]
    [Arguments("Bash")]
    [Arguments("")]
    [Arguments(null)]
    public async Task Names_outside_the_catalogue_do_not_match(string? name) {
        await Assert.That(KcapToolCatalogue.Match(name)).IsNull();
    }

    [Test]
    public async Task Every_entry_has_a_label_and_a_distinct_tool_name() {
        var tools = KcapToolCatalogue.Entries.Select(e => e.Tool).ToList();
        await Assert.That(tools.Distinct(StringComparer.Ordinal).Count()).IsEqualTo(tools.Count);
        foreach (var entry in KcapToolCatalogue.Entries) {
            await Assert.That(entry.Label.Length).IsGreaterThan(0);
            await Assert.That(entry.Tool).IsEqualTo(entry.Tool.ToLowerInvariant());
        }
    }

    [Test]
    [Arguments("publish_artefact", ToolCategory.Artefact, "Published page", "title", ToolCardKind.Page)]
    [Arguments("declare_work_item", ToolCategory.Work, "Attached work item", "issue_key", ToolCardKind.None)]
    [Arguments("save_memory", ToolCategory.Memory, "Saved memory", "slug", ToolCardKind.None)]
    [Arguments("search_sessions", ToolCategory.Session, "Searched sessions", "query", ToolCardKind.None)]
    [Arguments("start_agent", ToolCategory.Flow, "Started hosted agent", "prompt", ToolCardKind.Agent)]
    [Arguments("start_review_flow", ToolCategory.Flow, "Started review flow", "kind", ToolCardKind.Flow)]
    [Arguments("await_artefact_responses", ToolCategory.Artefact, "Waiting for answers", null, ToolCardKind.None)]
    public async Task Entries_carry_category_label_detail_key_and_card_kind(string tool, ToolCategory category, string label, string? detailKey, ToolCardKind card) {
        var entry = KcapToolCatalogue.Match(tool)!;
        await Assert.That(entry.Category).IsEqualTo(category);
        await Assert.That(entry.Label).IsEqualTo(label);
        await Assert.That(entry.DetailKey).IsEqualTo(detailKey);
        await Assert.That(entry.Card).IsEqualTo(card);
    }

    /// A kcap tool the catalogue does not list still reads as ours: category from the server
    /// segment, label from the humanised tool name.
    [Test]
    public async Task An_unlisted_kcap_tool_gets_a_humanised_label_and_its_servers_category() {
        var entry = KcapToolCatalogue.Match("mcp__plugin_kcap_kcap-workitems__retract_work_breakdown")!;
        await Assert.That(entry.Category).IsEqualTo(ToolCategory.Work);
        await Assert.That(entry.Label).IsEqualTo("Retract work breakdown");
        await Assert.That(entry.DetailKey).IsNull();
        await Assert.That(entry.Card).IsEqualTo(ToolCardKind.None);
    }

    [Test]
    [Arguments("mcp__linear__create_issue", "Linear · create issue")]
    [Arguments("mcp__plugin_linear_linear__save_issue", "Linear · save issue")]
    [Arguments("mcp__github__create_pull_request", "Github · create pull request")]
    public async Task A_foreign_mcp_name_reads_as_server_and_tool(string name, string expected) {
        await Assert.That(KcapToolCatalogue.ForeignLabel(name)).IsEqualTo(expected);
    }

    [Test]
    [Arguments("Bash")]
    [Arguments("mcp__")]
    [Arguments("mcp__x")]
    [Arguments("")]
    [Arguments(null)]
    public async Task A_name_without_the_mcp_shape_has_no_foreign_label(string? name) {
        await Assert.That(KcapToolCatalogue.ForeignLabel(name)).IsNull();
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet build test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj 2>&1 | grep -E 'error' | head`
Expected: errors naming `KcapToolCatalogue`, `KcapToolEntry`, `ToolCardKind` as missing.

- [ ] **Step 4: Create the enum and the entry record**

`src/Capacitor.App/ViewModels/ToolCardKind.cs`:

```csharp
namespace Capacitor.App.ViewModels;

/// Which card, if any, a settled call of this tool becomes. None is the ordinary row.
public enum ToolCardKind { None, Page, Document, Flow, Agent }
```

`src/Capacitor.App/ViewModels/KcapToolEntry.cs`:

```csharp
namespace Capacitor.App.ViewModels;

/// One kcap MCP tool as the chat shows it: the bare tool name, the category its rows fold under,
/// the verb phrase the row opens with, the argument that becomes the row's detail, and whether a
/// settled call becomes a card.
public sealed record KcapToolEntry(string Tool, ToolCategory Category, string Label, string? DetailKey, ToolCardKind Card = ToolCardKind.None);
```

- [ ] **Step 5: Create the catalogue**

`src/Capacitor.App/ViewModels/KcapToolCatalogue.cs`:

```csharp
using System.Collections.Frozen;

namespace Capacitor.App.ViewModels;

/// The kcap MCP tools in the product's words. Matching is by bare tool name: Claude prefixes the
/// server (`mcp__plugin_kcap_kcap-plans__x`), Codex records `x` alone. A prefixed name must name a
/// kcap server, so another vendor's `save_memory` is not ours.
public static class KcapToolCatalogue {
    const string McpPrefix = "mcp__";
    const string Separator = "__";

    internal static readonly KcapToolEntry[] Entries = [
        // artefacts
        new("publish_artefact",          ToolCategory.Artefact, "Published page",           "title",  ToolCardKind.Page),
        new("set_artefact_visibility",   ToolCategory.Artefact, "Shared page",              "visibility"),
        new("await_artefact_responses",  ToolCategory.Artefact, "Waiting for answers",      null),
        new("get_artefact_results",      ToolCategory.Artefact, "Read answers",             null),
        new("close_artefact_responses",  ToolCategory.Artefact, "Closed answers",           null),
        new("list_my_artefacts",         ToolCategory.Artefact, "Listed pages",             null),
        // plans
        new("declare_plan_document",     ToolCategory.Plan,     "Declared document",        "path",   ToolCardKind.Document),
        new("set_plan_tasks",            ToolCategory.Plan,     "Set plan tasks",           null),
        new("update_plan_task",          ToolCategory.Plan,     "Updated task",             "status"),
        new("get_plan",                  ToolCategory.Plan,     "Read the plan",            null),
        // work items
        new("declare_work_item",         ToolCategory.Work,     "Attached work item",       "issue_key"),
        new("declare_loose_end",         ToolCategory.Work,     "Left a loose end",         "title"),
        new("close_loose_end",           ToolCategory.Work,     "Closed a loose end",       null),
        new("get_next_work",             ToolCategory.Work,     "Checked what to do next",  null),
        new("declare_work_breakdown",    ToolCategory.Work,     "Declared work breakdown",  null),
        new("declare_work_relation",     ToolCategory.Work,     "Declared work relation",   null),
        new("merge_work_item",           ToolCategory.Work,     "Merged work items",        null),
        new("detach_work_item",          ToolCategory.Work,     "Detached work item",       null),
        new("dismiss_next_work",         ToolCategory.Work,     "Dismissed a suggestion",   null),
        new("get_session_work_items",    ToolCategory.Work,     "Read work items",          null),
        new("request_work_item_eval",    ToolCategory.Work,     "Requested an evaluation",  null),
        // memory and knowledge
        new("save_memory",               ToolCategory.Memory,   "Saved memory",             "slug"),
        new("search_memories",           ToolCategory.Memory,   "Searched memory",          "query"),
        new("get_memory",                ToolCategory.Memory,   "Read memory",              "slug"),
        new("update_memory",             ToolCategory.Memory,   "Updated memory",           "slug"),
        new("archive_memory",            ToolCategory.Memory,   "Archived memory",          "slug"),
        new("search_facts",              ToolCategory.Memory,   "Searched facts",           "query"),
        new("list_facts",                ToolCategory.Memory,   "Listed facts",             null),
        new("get_skill",                 ToolCategory.Memory,   "Read skill",               "slug"),
        new("list_skills",               ToolCategory.Memory,   "Listed skills",            null),
        // sessions, handoff, analytics, review
        new("search_sessions",           ToolCategory.Session,  "Searched sessions",        "query"),
        new("get_session_transcript",    ToolCategory.Session,  "Read session",             "session_id"),
        new("get_session_summary",       ToolCategory.Session,  "Read session summary",     "session_id"),
        new("list_repo_sessions",        ToolCategory.Session,  "Listed sessions",          null),
        new("get_declared_plans",        ToolCategory.Session,  "Read declared plans",      null),
        new("list_repo_plans",           ToolCategory.Session,  "Listed plans",             null),
        new("get_turn",                  ToolCategory.Session,  "Read a turn",              null),
        new("list_turns",                ToolCategory.Session,  "Listed turns",             null),
        new("continue_session",          ToolCategory.Session,  "Continued session",        "session_id"),
        new("query_analytics",           ToolCategory.Session,  "Ran analytics query",      "sql"),
        new("get_analytics_schema",      ToolCategory.Session,  "Read analytics schema",    null),
        new("search_context",            ToolCategory.Session,  "Searched session context", "query"),
        new("get_pr_summary",            ToolCategory.Session,  "Read PR summary",          null),
        new("list_pr_files",             ToolCategory.Session,  "Listed PR files",          null),
        new("get_file_context",          ToolCategory.Session,  "Read file context",        "path"),
        new("get_transcript",            ToolCategory.Session,  "Read transcript",          null),
        // flows and hosted agents
        new("start_review_flow",         ToolCategory.Flow,     "Started review flow",      "kind",   ToolCardKind.Flow),
        new("start_flow",                ToolCategory.Flow,     "Started flow",             "definition_id", ToolCardKind.Flow),
        new("start_agent",               ToolCategory.Flow,     "Started hosted agent",     "prompt", ToolCardKind.Agent),
        new("submit_review_round",       ToolCategory.Flow,     "Submitted review round",   null),
        new("get_review_flow_status",    ToolCategory.Flow,     "Checked flow status",      null),
        new("get_flow_status",           ToolCategory.Flow,     "Checked flow status",      null),
        new("close_review_flow",         ToolCategory.Flow,     "Closed flow",              null),
        new("close_flow",                ToolCategory.Flow,     "Closed flow",              null),
        new("send_to_participant",       ToolCategory.Flow,     "Sent to participant",      null),
        new("list_flow_definitions",     ToolCategory.Flow,     "Listed flow options",      null),
        new("list_reviewer_vendors",     ToolCategory.Flow,     "Listed flow options",      null),
        new("list_start_agent_options",  ToolCategory.Flow,     "Listed flow options",      null),
    ];

    static readonly FrozenDictionary<string, KcapToolEntry> ByTool =
        Entries.ToFrozenDictionary(e => e.Tool, StringComparer.Ordinal);

    /// Category per kcap server, for a kcap tool the table does not list.
    static readonly FrozenDictionary<string, ToolCategory> ServerCategories = new Dictionary<string, ToolCategory>(StringComparer.Ordinal) {
        ["kcap-artefacts"] = ToolCategory.Artefact,
        ["kcap-plans"]     = ToolCategory.Plan,
        ["kcap-workitems"] = ToolCategory.Work,
        ["kcap-memory"]    = ToolCategory.Memory,
        ["kcap-knowledge"] = ToolCategory.Memory,
        ["kcap-sessions"]  = ToolCategory.Session,
        ["kcap-handoff"]   = ToolCategory.Session,
        ["kcap-analytics"] = ToolCategory.Session,
        ["kcap-review"]    = ToolCategory.Session,
        ["kcap-flows"]     = ToolCategory.Flow,
    }.ToFrozenDictionary(StringComparer.Ordinal);

    public static KcapToolEntry? Match(string? toolName) {
        if (string.IsNullOrEmpty(toolName)) return null;
        var (server, tool) = Split(toolName);
        if (server is not null && !server.StartsWith("kcap", StringComparison.Ordinal)) return null;
        if (ByTool.TryGetValue(tool, out var entry)) return entry;
        if (server is not null && ServerCategories.TryGetValue(server, out var category))
            return new KcapToolEntry(tool, category, Humanise(tool), null);
        return null;
    }

    /// "Linear · create issue" for a prefixed name from another server; null for anything else.
    public static string? ForeignLabel(string? toolName) {
        if (string.IsNullOrEmpty(toolName)) return null;
        var (server, tool) = Split(toolName);
        if (server is null || server.Length == 0 || tool.Length == 0) return null;
        return $"{Humanise(server)} · {tool.Replace('_', ' ')}";
    }

    /// (server, tool) for `mcp__<server>__<tool>`; (null, name) for a bare name. A plugin server
    /// segment (`plugin_kcap_kcap-plans`, `plugin_linear_linear`) keeps its last `_`-separated token.
    static (string? Server, string Tool) Split(string name) {
        if (!name.StartsWith(McpPrefix, StringComparison.Ordinal)) return (null, name);
        var rest = name[McpPrefix.Length..];
        var cut = rest.LastIndexOf(Separator, StringComparison.Ordinal);
        if (cut <= 0) return ("", cut < 0 ? rest : rest[(cut + Separator.Length)..]);
        var server = rest[..cut];
        if (server.StartsWith("plugin_", StringComparison.Ordinal)) server = server[(server.LastIndexOf('_') + 1)..];
        return (server, rest[(cut + Separator.Length)..]);
    }

    static string Humanise(string token) {
        var words = token.Replace('_', ' ').Replace('-', ' ').Trim();
        return words.Length == 0 ? words : char.ToUpperInvariant(words[0]) + words[1..];
    }
}
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/KcapToolCatalogueTests/*"`
Expected: all pass. `Names_outside_the_catalogue_do_not_match` with `mcp__plugin_acme_acme__publish_artefact` passes because the server segment `acme` does not start with `kcap`.

- [ ] **Step 7: Commit**

```bash
git add src/Capacitor.App/ViewModels/ToolCardKind.cs src/Capacitor.App/ViewModels/KcapToolEntry.cs src/Capacitor.App/ViewModels/KcapToolCatalogue.cs src/Capacitor.App/ViewModels/ToolSummary.cs test/Capacitor.App.Tests.Unit/KcapToolCatalogueTests.cs
git commit -m "Add the kcap tool catalogue for chat rows (#1098)" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

### Task 2: Categories, phrases, chips, icons and detail keys

**Files:**
- Modify: `src/Capacitor.App/ViewModels/ToolSummary.cs` (`Categorize`, `ChipLabel`)
- Modify: `src/Capacitor.App/Views/ToolCategoryIcons.cs`
- Modify: `src/Capacitor.App/ViewModels/ToolDetail.cs`
- Test: `test/Capacitor.App.Tests.Unit/ToolSummaryTests.cs`, `test/Capacitor.App.Tests.Unit/ToolCategoryIconsTests.cs`, `test/Capacitor.App.Tests.Unit/ToolDetailTests.cs`

**Interfaces:**
- Consumes: `KcapToolCatalogue.Match`.
- Produces: `ToolSummary.Categorize` returns the catalogue category for a kcap name; `ToolSummary.ChipLabel` for the five new categories; `ToolCategoryIcons.ForCategory` for them; `static string ToolDetail.ForKey(string? inputJson, string? key)`; `static string ToolDetail.FirstString(string? inputJson)`.

- [ ] **Step 1: Write the failing tests**

Append to `test/Capacitor.App.Tests.Unit/ToolSummaryTests.cs` inside the class:

```csharp
    [Test]
    [Arguments("mcp__plugin_kcap_kcap-workitems__declare_work_item", ToolCategory.Work)]
    [Arguments("declare_work_item", ToolCategory.Work)]
    [Arguments("mcp__kcap-artefacts__publish_artefact", ToolCategory.Artefact)]
    [Arguments("save_memory", ToolCategory.Memory)]
    [Arguments("search_sessions", ToolCategory.Session)]
    [Arguments("start_agent", ToolCategory.Flow)]
    [Arguments("mcp__plugin_kcap_kcap-plans__update_plan_task", ToolCategory.Plan)]
    public async Task Kcap_tools_categorize_through_the_catalogue(string name, ToolCategory expected) {
        await Assert.That(ToolSummary.Categorize(name, """{"x":1}""")).IsEqualTo(expected);
    }

    [Test]
    public async Task Kcap_categories_describe_and_chip_in_product_words() {
        await Assert.That(ToolSummary.Describe([ToolCategory.Artefact])).IsEqualTo("Worked on a page");
        await Assert.That(ToolSummary.Describe([ToolCategory.Work, ToolCategory.Memory, ToolCategory.Session, ToolCategory.Flow, ToolCategory.Flow]))
            .IsEqualTo("Tracked work, used team memory, recalled sessions, ran flows");
        await Assert.That(ToolSummary.ChipLabel(ToolCategory.Artefact)).IsEqualTo("Page");
        await Assert.That(ToolSummary.ChipLabel(ToolCategory.Work)).IsEqualTo("Work");
        await Assert.That(ToolSummary.ChipLabel(ToolCategory.Memory)).IsEqualTo("Memory");
        await Assert.That(ToolSummary.ChipLabel(ToolCategory.Session)).IsEqualTo("Recall");
        await Assert.That(ToolSummary.ChipLabel(ToolCategory.Flow)).IsEqualTo("Flow");
    }
```

The existing `Unknown_and_mcp_names_are_other` test keeps `mcp__github__create_issue` as `Other`, which still holds.

Append to `test/Capacitor.App.Tests.Unit/ToolCategoryIconsTests.cs` inside the class:

```csharp
    [Test]
    public async Task Every_kcap_category_has_its_own_icon_distinct_from_the_generic_one() {
        var generic = ToolCategoryIcons.ForCategory(ToolCategory.Other);
        ToolCategory[] ours = [ToolCategory.Artefact, ToolCategory.Work, ToolCategory.Memory, ToolCategory.Session, ToolCategory.Flow];
        var data = ours.Select(ToolCategoryIcons.ForCategory).ToList();
        await Assert.That(data.All(d => d.Length > 0 && d != generic)).IsTrue();
        await Assert.That(data.Distinct(StringComparer.Ordinal).Count()).IsEqualTo(data.Count);
    }
```

Append to `test/Capacitor.App.Tests.Unit/ToolDetailTests.cs` inside the class:

```csharp
    [Test]
    public async Task ForKey_reads_the_named_string_first_line_elided_and_nothing_else() {
        await Assert.That(ToolDetail.ForKey("""{"issue_key":"AI-3084","title":"x"}""", "issue_key")).IsEqualTo("AI-3084");
        await Assert.That(ToolDetail.ForKey("""{"prompt":"Fix the flaky test\nthen push"}""", "prompt")).IsEqualTo("Fix the flaky test");
        await Assert.That(ToolDetail.ForKey("""{"title":"x"}""", "issue_key")).IsEqualTo("");
        await Assert.That(ToolDetail.ForKey("""{"title":"x"}""", null)).IsEqualTo("");
        await Assert.That(ToolDetail.ForKey("not json", "title")).IsEqualTo("");
        await Assert.That(ToolDetail.ForKey("""{"query":"%s"}""", "query").Length).IsLessThanOrEqualTo(80);
    }

    [Test]
    public async Task FirstString_takes_the_first_non_empty_string_property() {
        await Assert.That(ToolDetail.FirstString("""{"n":1,"team":"","title":"Add tests","body":"long"}""")).IsEqualTo("Add tests");
        await Assert.That(ToolDetail.FirstString("""{"n":1}""")).IsEqualTo("");
        await Assert.That(ToolDetail.FirstString(null)).IsEqualTo("");
    }
```

Replace the `"%s"` placeholder in the elision line with a 120-character string literal (for example `new string('a', 120)` built in the test before the assertion) so the assertion exercises the cap.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/ToolSummaryTests/*"`
Expected: `Kcap_tools_categorize_through_the_catalogue` fails (returns `Other`); `Kcap_categories_describe_and_chip_in_product_words` fails on `ChipLabel` returning "Tool". `ToolDetailTests` fail to build on `ForKey`/`FirstString`.

- [ ] **Step 3: Implement**

In `ToolSummary.Categorize`, make the catalogue the first word:

```csharp
    public static ToolCategory Categorize(string name, string? inputJson) {
        if (KcapToolCatalogue.Match(name) is { } kcap) return kcap.Category;
        var category = Names.TryGetValue(name, out var known) ? known : ToolCategory.Other;
```

(the rest of the method unchanged). In `ChipLabel` add before the default arm:

```csharp
        ToolCategory.Artefact  => "Page",
        ToolCategory.Work      => "Work",
        ToolCategory.Memory    => "Memory",
        ToolCategory.Session   => "Recall",
        ToolCategory.Flow      => "Flow",
```

In `ToolCategoryIcons.ForCategory` add before the default arm:

```csharp
        // Page: a document with a folded corner and a globe-less frame, distinct from Read's file.
        ToolCategory.Artefact  => "M5,4 H19 V20 H5 Z M5,8 H19 M8,12 H16 M8,15 H13",
        // Work: a ticket with a key line.
        ToolCategory.Work      => "M4,6 H20 V18 H4 Z M4,10 H20 M8,14 H12",
        // Memory: a bookmark.
        ToolCategory.Memory    => "M7,3 H17 V21 L12,17 L7,21 Z",
        // Recall: a clock face with a reverse arrow.
        ToolCategory.Session   => "M12,7 V12 L15,14 M4,12 A8,8 0 1 0 6.5,6.3 M4,4 V8 H8",
        // Flow: three nodes in a chain.
        ToolCategory.Flow      => "M4,12 H9 M15,12 H20 M9,9 H15 V15 H9 Z M2,10 H4 V14 H2 Z M20,10 H22 V14 H20 Z",
```

In `ToolDetail` add two methods after `From`:

```csharp
    /// The one argument a catalogued kcap row shows, first line only, elided like any detail.
    public static string ForKey(string? inputJson, string? key) {
        if (string.IsNullOrEmpty(inputJson) || string.IsNullOrEmpty(key)) return "";
        try {
            using var doc = JsonDocument.Parse(inputJson);
            if (!doc.RootElement.IsObject) return "";
            return doc.RootElement.Str(key) is { } s && s.Trim().Length > 0 ? TextElision.End(FirstLine(s), MaxLength) : "";
        } catch (JsonException) {
            return "";
        }
    }

    /// For a tool nothing describes: the first string argument, in declaration order.
    public static string FirstString(string? inputJson) {
        if (string.IsNullOrEmpty(inputJson)) return "";
        try {
            using var doc = JsonDocument.Parse(inputJson);
            if (!doc.RootElement.IsObject) return "";
            foreach (var property in doc.RootElement.EnumerateObject())
                if (property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() is { } s && s.Trim().Length > 0)
                    return TextElision.End(FirstLine(s), MaxLength);
        } catch (JsonException) { }
        return "";
    }
```

`EnumerateObject` yields `JsonProperty`, which `JsonElementExtensions` does not wrap, so the `ValueKind` check there is the one permitted read; everything else goes through `Str`.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/ToolSummaryTests/*"` then the same for `ToolCategoryIconsTests` and `ToolDetailTests`.
Expected: all pass, including the pre-existing `Every_declared_name_maps_to_its_row_case_insensitively_and_nothing_else_is_declared` (the `Names` table is untouched).

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.App/ViewModels/ToolSummary.cs src/Capacitor.App/Views/ToolCategoryIcons.cs src/Capacitor.App/ViewModels/ToolDetail.cs test/Capacitor.App.Tests.Unit/ToolSummaryTests.cs test/Capacitor.App.Tests.Unit/ToolCategoryIconsTests.cs test/Capacitor.App.Tests.Unit/ToolDetailTests.cs
git commit -m "Phrase, chip and icon the kcap tool categories (#1098)" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

### Task 3: The card model and its builders

**Files:**
- Create: `src/Capacitor.App/ViewModels/ToolCard.cs`
- Create: `src/Capacitor.App/ViewModels/ToolCards.cs`
- Test: `test/Capacitor.App.Tests.Unit/ToolCardsTests.cs`

**Interfaces:**
- Consumes: `ToolCardKind`.
- Produces: `record ToolCard(ToolCardKind Kind, string Title, string Name, string Meta, string? Url, string? DocumentPath)`; `static ToolCard? ToolCards.Build(ToolCardKind kind, string? inputJson, string? resultText)`; `static string ToolCards.Audience(string? visibility)`; `static string ToolCards.FileName(string path)`.

- [ ] **Step 1: Write the failing tests**

Create `test/Capacitor.App.Tests.Unit/ToolCardsTests.cs`:

```csharp
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
        var flow = ToolCards.Build(ToolCardKind.Flow, """{"kind":"code-review","reviewer_vendor":"codex"}""", "started")!;
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
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet build test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj 2>&1 | grep -E 'error' | head`
Expected: errors naming `ToolCards` and `ToolCard`.

- [ ] **Step 3: Create the record**

`src/Capacitor.App/ViewModels/ToolCard.cs`:

```csharp
namespace Capacitor.App.ViewModels;

/// What a producing call left behind, as the chat shows it: the verb line ("Published page"), the
/// thing's own name, one meta line, and whichever target it can open. Url is a page; DocumentPath
/// is a declared document as the declaration spelled it.
public sealed record ToolCard(ToolCardKind Kind, string Title, string Name, string Meta, string? Url, string? DocumentPath);
```

- [ ] **Step 4: Create the builders**

`src/Capacitor.App/ViewModels/ToolCards.cs`:

```csharp
using System.Text.Json;

namespace Capacitor.App.ViewModels;

/// Builds a card from a settled call. A page card needs the server's whole artefact object in the
/// result; the result rides the transcript capped at 4096 characters, so a cut body builds nothing
/// and the call stays a row.
public static class ToolCards {
    const int NameCap = 80;

    public static ToolCard? Build(ToolCardKind kind, string? inputJson, string? resultText) => kind switch {
        ToolCardKind.Page     => Page(resultText),
        ToolCardKind.Document => Document(inputJson),
        ToolCardKind.Flow     => Flow(inputJson),
        ToolCardKind.Agent    => Agent(inputJson),
        _                     => null,
    };

    public static string Audience(string? visibility) => visibility switch {
        "org"    => "Org",
        "scoped" => "Shared",
        _        => "Private",
    };

    /// The declaring machine's path, so the name is cut on either separator.
    public static string FileName(string path) => path[(path.LastIndexOfAny(['/', '\\']) + 1)..];

    static ToolCard? Page(string? resultText) {
        if (string.IsNullOrWhiteSpace(resultText)) return null;
        try {
            using var doc = JsonDocument.Parse(resultText);
            if (!doc.RootElement.IsObject || doc.RootElement.Obj("artefact") is not { } artefact) return null;
            var title = artefact.Str("title");
            var url = artefact.Str("url");
            if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(url)) return null;
            var version = artefact.Int("latest_version") ?? 1;
            return new ToolCard(ToolCardKind.Page, version > 1 ? "Updated page" : "Published page",
                TextElision.End(title, NameCap), $"v{version} · {Audience(artefact.Str("visibility"))}", url, null);
        } catch (JsonException) {
            return null;
        }
    }

    static ToolCard? Document(string? inputJson) {
        if (Args(inputJson) is not { } args) return null;
        var path = args.Str("path");
        if (string.IsNullOrWhiteSpace(path)) return null;
        var title = args.Str("kind") switch {
            "plan"   => "Declared plan",
            "spec"   => "Declared spec",
            "design" => "Declared design doc",
            _        => "Declared document",
        };
        return new ToolCard(ToolCardKind.Document, title, FileName(path), path, null, path);
    }

    static ToolCard? Flow(string? inputJson) {
        if (Args(inputJson) is not { } args) return null;
        var kind = args.Str("kind") ?? args.Str("definition_id") ?? "";
        var title = kind.Length == 0 ? "Started flow" : $"Started {kind} flow";
        return new ToolCard(ToolCardKind.Flow, title, kind, args.Str("reviewer_vendor") ?? "", null, null);
    }

    static ToolCard? Agent(string? inputJson) {
        if (Args(inputJson) is not { } args) return null;
        var prompt = args.Str("prompt") ?? "";
        var firstLine = prompt.Split(['\r', '\n'], 2)[0].Trim();
        return new ToolCard(ToolCardKind.Agent, "Started hosted agent", TextElision.End(firstLine, NameCap), args.Str("vendor") ?? "", null, null);
    }

    static JsonElement? Args(string? inputJson) {
        if (string.IsNullOrEmpty(inputJson)) return null;
        try {
            using var doc = JsonDocument.Parse(inputJson);
            return doc.RootElement.IsObject ? doc.RootElement.Clone() : null;
        } catch (JsonException) {
            return null;
        }
    }
}
```

`Obj`, `Str` and `Int` are the `JsonElementExtensions` readers; if `Int` does not exist under that name in `Capacitor.Cli.Core`, use the integer reader the extensions do provide (check `JsonElementExtensions.cs` and keep the `?? 1` default).

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/ToolCardsTests/*"`
Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add src/Capacitor.App/ViewModels/ToolCard.cs src/Capacitor.App/ViewModels/ToolCards.cs test/Capacitor.App.Tests.Unit/ToolCardsTests.cs
git commit -m "Build page and document cards from settled kcap calls (#1098)" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

### Task 4: Rows carry a label, calls carry a card, groups keep cards visible

**Files:**
- Modify: `src/Capacitor.App/ViewModels/ChatItems.cs` (`ToolCallItem`, `ToolGroupItem`)
- Modify: `src/Capacitor.App/ViewModels/ChatTabViewModel.cs:46-47,434-447,916-937`
- Test: `test/Capacitor.App.Tests.Unit/ToolGroupItemTests.cs`, `test/Capacitor.App.Tests.Unit/ChatTabViewModelTests.cs`

**Interfaces:**
- Consumes: `KcapToolCatalogue.Match`, `KcapToolCatalogue.ForeignLabel`, `ToolDetail.ForKey`, `ToolDetail.FirstString`, `ToolCards.Build`, `ToolCard`, `ToolCardKind`.
- Produces: `ToolCallItem(string name, string detail, ToolCategory category, string? label = null, ToolCardKind cardKind = ToolCardKind.None, string? inputJson = null)` with `Label`, `HasLabel`, `CardKind`, `InputJson`, `Card`, `HasCard`, `CanOpenCard`, `OpenCardCommand`, `void SetCard(ToolCard card, Action? open)`; `ChatTabViewModel` constructors gain `Action<ToolCard>? openCard = null` as the last optional parameter.

- [ ] **Step 1: Write the failing group test**

Append to `test/Capacitor.App.Tests.Unit/ToolGroupItemTests.cs` inside the class:

```csharp
    /// A card is the point of the group; folding hides settled rows, never a card.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_folded_group_keeps_its_cards_beside_the_live_calls() {
        await RunOnUiAsync(async () => {
            var group = new ToolGroupItem();
            var read = Call("Read", ToolCategory.Read);
            var publish = new ToolCallItem("publish_artefact", "Retention brief", ToolCategory.Artefact, "Published page", ToolCardKind.Page, "{}");
            var live = Call("Bash", ToolCategory.Command);
            group.Add(read);
            group.Add(publish);
            group.Add(live);

            publish.SetCard(new ToolCard(ToolCardKind.Page, "Published page", "Retention brief", "v1 · Org", "https://x/artefacts/1", null), open: null);
            publish.Outcome = ToolOutcome.Done;
            read.Outcome = ToolOutcome.Done;

            await Assert.That(group.IsExpanded).IsFalse();
            await Assert.That(group.VisibleCalls).IsEquivalentTo(new[] { publish, live }, CollectionOrdering.Matching);
            await Assert.That(group.LiveCalls).IsEquivalentTo(new[] { live });

            group.Toggle();
            await Assert.That(group.VisibleCalls).IsEquivalentTo(new[] { read, publish, live }, CollectionOrdering.Matching);
        });
    }

    /// A card attached after the call settled still surfaces in the folded list.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_card_set_after_settling_joins_the_folded_list() {
        await RunOnUiAsync(async () => {
            var group = new ToolGroupItem();
            var a = Call("Read", ToolCategory.Read);
            var b = new ToolCallItem("declare_plan_document", "x.md", ToolCategory.Plan, "Declared document", ToolCardKind.Document, "{}");
            group.Add(a);
            group.Add(b);
            a.Outcome = ToolOutcome.Done;
            b.Outcome = ToolOutcome.Done;
            await Assert.That(group.HasVisibleCalls).IsFalse();

            b.SetCard(new ToolCard(ToolCardKind.Document, "Declared plan", "x.md", "x.md", null, "x.md"), open: null);

            await Assert.That(group.VisibleCalls).IsEquivalentTo(new[] { b });
            await Assert.That(group.HasVisibleCalls).IsTrue();
        });
    }
```

- [ ] **Step 2: Write the failing chat test**

In `test/Capacitor.App.Tests.Unit/ChatTabViewModelTests.cs` add two constants beside `PlanResultLine`:

```csharp
    const string PublishCallLine = """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_pub","name":"mcp__plugin_kcap_kcap-artefacts__publish_artefact","input":{"title":"Retention brief","html":"<p>x</p>","visibility":"org"}}]}}""";
    const string PublishResultLine = """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"toolu_pub","content":"{\"artefact\":{\"artefact_id\":\"01ec\",\"title\":\"Retention brief\",\"owner_user_id\":\"u1\",\"visibility\":\"org\",\"latest_version\":1,\"updated_at\":\"2026-10-07T10:00:00Z\",\"is_owner\":true,\"url\":\"https://kurrent.kcap.ai/artefacts/01ec\"}}"}]}}""";
    const string WorkItemCallLine = """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_wi","name":"mcp__plugin_kcap_kcap-workitems__declare_work_item","input":{"issue_key":"AI-3084"}}]}}""";
    const string LinearCallLine = """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_lin","name":"mcp__plugin_linear_linear__save_issue","input":{"title":"Add tests"}}]}}""";
```

and these tests, next to `A_plan_write_in_the_transcript_is_reported_once_its_result_is_read`:

```csharp
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_kcap_call_reads_as_a_labelled_row_and_a_publish_becomes_a_card_when_its_result_lands() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var path = Tmp.CreateFile("t.jsonl", [WorkItemCallLine, PublishCallLine]);
            await h.PushAsync(Dto(path));

            var group = Group(h.Chat, 0);
            var work = group.Calls[0];
            await Assert.That(work.Label).IsEqualTo("Attached work item");
            await Assert.That(work.Detail).IsEqualTo("AI-3084");
            await Assert.That(work.Category).IsEqualTo(ToolCategory.Work);
            var publish = group.Calls[1];
            await Assert.That(publish.Label).IsEqualTo("Published page");
            await Assert.That(publish.Detail).IsEqualTo("Retention brief");
            await Assert.That(publish.HasCard).IsFalse();

            File.AppendAllText(path, PublishResultLine + "\n");
            await h.TickAsync();

            await Assert.That(publish.Outcome).IsEqualTo(ToolOutcome.Done);
            await Assert.That(publish.HasCard).IsTrue();
            await Assert.That(publish.Card!.Meta).IsEqualTo("v1 · Org");
            await Assert.That(publish.CanOpenCard).IsTrue();
            await publish.OpenCardCommand!.Execute();
            await Assert.That(h.Opener.Opened).IsEquivalentTo(new[] { "https://kurrent.kcap.ai/artefacts/01ec" });
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_foreign_mcp_call_reads_as_server_and_tool_with_its_first_argument() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            await h.PushAsync(Dto(Tmp.CreateFile("t.jsonl", [LinearCallLine])));

            var call = Group(h.Chat, 0).Calls[0];
            await Assert.That(call.Label).IsEqualTo("Linear · save issue");
            await Assert.That(call.Detail).IsEqualTo("Add tests");
            await Assert.That(call.Category).IsEqualTo(ToolCategory.Other);
            await h.TeardownAsync();
        });
    }

    /// A page card follows the opener the workspace hands in when there is one, so slice 2 can
    /// route a document into the tab without the chat knowing about tabs.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_injected_card_opener_takes_precedence_over_the_browser() {
        await RunOnUiAsync(async () => {
            ToolCard? opened = null;
            var h = Claude(openCard: card => opened = card);
            var path = Tmp.CreateFile("t.jsonl", [PublishCallLine, PublishResultLine]);
            await h.PushAsync(Dto(path));

            var publish = Group(h.Chat, 0).Calls[0];
            await publish.OpenCardCommand!.Execute();
            await Assert.That(opened?.Url).IsEqualTo("https://kurrent.kcap.ai/artefacts/01ec");
            await Assert.That(h.Opener.Opened).IsEmpty();
            await h.TeardownAsync();
        });
    }
```

`Claude()` is the suite's existing harness factory; give it an optional `Action<ToolCard>? openCard = null` parameter that it passes to the `Harness` constructor, and give `Harness` the same optional parameter, passed to `ChatTabViewModel` as `openCard: openCard`. `h.Opener.Opened` is `RecordingOpener`'s list of opened URLs (check the fake's member name and use it).

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet build test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj 2>&1 | grep -E 'error' | head`
Expected: errors on the six-argument `ToolCallItem` constructor, `SetCard`, `Label`, `HasCard`, `OpenCardCommand`, and the `openCard` parameter.

- [ ] **Step 4: Extend `ToolCallItem`**

In `src/Capacitor.App/ViewModels/ChatItems.cs` replace the `ToolCallItem` declaration and add the members. Keep the existing `Outcome` and `IsAwaitingPermission` blocks as they are; the new parts are:

```csharp
public sealed class ToolCallItem(string name, string detail, ToolCategory category, string? label = null,
        ToolCardKind cardKind = ToolCardKind.None, string? inputJson = null) : ChatItemViewModel {
    public string Name { get; } = name;
    public string Detail { get; } = detail;
    public ToolCategory Category { get; } = category;
    /// The verb phrase a kcap or foreign MCP row opens with ("Attached work item"); null for a
    /// built-in tool, whose row is the detail alone.
    public string? Label { get; } = label;
    public bool HasLabel => !string.IsNullOrEmpty(Label);
    public ToolCardKind CardKind { get; } = cardKind;
    /// Kept so the card can be built when the result arrives.
    internal string? InputJson { get; } = inputJson;

    /// What the row shows: detail when present, otherwise the tool name.
    public string LineText => string.IsNullOrEmpty(Detail) ? Name : Detail;

    /// True when the transcript carried a useful detail (brighter paint than a bare name).
    public bool HasDetail => !string.IsNullOrEmpty(Detail);

    /// A question's detail is the question itself — prose, read whole and wrapped. Every other
    /// detail is a command or a path, which the row keeps to one line and elides.
    public bool DetailIsProse => Category == ToolCategory.Question;

    ToolCard? _card;
    public ToolCard? Card {
        get => _card;
        private set {
            if (ReferenceEquals(_card, value)) return;
            _card = value;
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(HasCard));
            this.RaisePropertyChanged(nameof(CanOpenCard));
        }
    }
    public bool HasCard => _card is not null;
    public bool CanOpenCard => OpenCardCommand is not null;
    public ReactiveCommand<Unit, Unit>? OpenCardCommand { get; private set; }

    /// Set before Outcome so a group sees the card the moment the call settles.
    public void SetCard(ToolCard card, Action? open) {
        OpenCardCommand = open is null ? null : ReactiveCommand.Create(open);
        this.RaisePropertyChanged(nameof(OpenCardCommand));
        Card = card;
    }
```

- [ ] **Step 5: Keep cards visible in a folded `ToolGroupItem`**

In the same file, inside `ToolGroupItem`:

```csharp
    readonly AvaloniaList<ToolCallItem> _calls = new();
    readonly AvaloniaList<ToolCallItem> _live = new();
    /// What a folded group shows: the live calls and every card, in call order.
    readonly AvaloniaList<ToolCallItem> _folded = new();

    public IAvaloniaReadOnlyList<ToolCallItem> Calls => _calls;
    public IAvaloniaReadOnlyList<ToolCallItem> LiveCalls => _live;
    public IAvaloniaReadOnlyList<ToolCallItem> VisibleCalls =>
        _calls.Count <= 1 || _isExpanded ? _calls : _folded;
```

Replace `Add`:

```csharp
    public void Add(ToolCallItem call) {
        _calls.Add(call);
        RefreshLoneChrome();
        this.RaisePropertyChanged(nameof(ShowsSummaryHeader));
        call.PropertyChanged += OnCallChanged;
        if (call.IsSettled) {
            if (call.HasCard) _folded.Add(call);
            Recompute();
            return;
        }
        _live.Add(call);
        _folded.Add(call);
        // After the add: a folded group's VisibleCalls is _folded, and HasVisibleCalls read before
        // the add would publish false for the call's whole run.
        NotifyVisible();
    }
```

Replace `OnCallChanged`:

```csharp
    void OnCallChanged(object? sender, PropertyChangedEventArgs e) {
        if (sender is not ToolCallItem call) return;
        if (e.PropertyName == nameof(ToolCallItem.Card)) {
            if (call.HasCard && !_folded.Contains(call)) InsertInCallOrder(_folded, call);
            NotifyVisible();
            return;
        }
        if (e.PropertyName != nameof(ToolCallItem.Outcome) || !call.IsSettled) return;
        _live.Remove(call);
        if (!call.HasCard) _folded.Remove(call);
        Recompute();
    }

    void InsertInCallOrder(AvaloniaList<ToolCallItem> list, ToolCallItem call) {
        var index = _calls.IndexOf(call);
        var at = 0;
        while (at < list.Count && _calls.IndexOf(list[at]) < index) at++;
        list.Insert(at, call);
    }
```

The handler stays attached after settling because a card can arrive later; `ToolCallItem` is owned by the group for the chat's lifetime, so no leak.

- [ ] **Step 6: Wire the catalogue and the card into `ChatTabViewModel`**

Field, beside `_planActivity` (line 46):

```csharp
    readonly Action<ToolCard>? _openCard;
```

Both constructors gain `Action<ToolCard>? openCard = null` as their last parameter; the first passes `openCard: openCard` to the second, and the second assigns `_openCard = openCard;` next to `_planActivity = planActivity;`.

Replace the `ToolCall` case body (line 916 onward):

```csharp
                    case AcpEventKind.ToolCall: {
                        _openShell = null;
                        var name = e.ToolName ?? "tool";
                        var entry = KcapToolCatalogue.Match(name);
                        var category = entry?.Category ?? ToolSummary.Categorize(name, e.ToolInputJson);
                        var label = entry?.Label ?? KcapToolCatalogue.ForeignLabel(name);
                        var detail = entry is not null ? ToolDetail.ForKey(e.ToolInputJson, entry.DetailKey)
                            : label is not null ? FirstOrAny(e.ToolInputJson, category)
                            : ToolDetail.From(e.ToolInputJson, _root, category);
                        var item = new ToolCallItem(name, detail, category, label, entry?.Card ?? ToolCardKind.None, e.ToolInputJson);
```

and keep the rest of the case (`_pendingTools[id] = item; …`) unchanged. Add the helper near `_root`:

```csharp
    /// A foreign MCP call: the usual detail keys first, then whatever string it was given.
    string FirstOrAny(string? inputJson, ToolCategory category) {
        var known = ToolDetail.From(inputJson, _root, category);
        return known.Length > 0 ? known : ToolDetail.FirstString(inputJson);
    }

    Action? CardOpener(ToolCard card) {
        if (card.Url is null && card.DocumentPath is null) return null;
        if (_openCard is { } open) return () => open(card);
        if (card.Url is { } url) return () => LinkPolicy.Open(_opener, url);
        return null;
    }
```

Replace the `ToolResult` case:

```csharp
                    case AcpEventKind.ToolResult:
                        if (e.ToolCallId is not { } resultId) break;
                        _settledTools.Add(resultId);
                        NoteQuestionResult(resultId, e.TimestampIso);
                        if (_pendingTools.Remove(resultId, out var call)) {
                            if (!e.ToolIsError && call.CardKind != ToolCardKind.None
                                && ToolCards.Build(call.CardKind, call.InputJson, e.ToolResult) is { } card)
                                call.SetCard(card, CardOpener(card));
                            call.Outcome = e.ToolIsError ? ToolOutcome.Error : ToolOutcome.Done;
                        }
                        break;
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/ToolGroupItemTests/*"` and the same for `ChatTabViewModelTests`.
Expected: the new tests pass and the existing ones stay green. `A_plan_write_in_the_transcript_is_reported_once_its_result_is_read` still passes: `update_plan_task` has no card kind, and `PlanActivity` is untouched.

- [ ] **Step 8: Commit**

```bash
git add src/Capacitor.App/ViewModels/ChatItems.cs src/Capacitor.App/ViewModels/ChatTabViewModel.cs test/Capacitor.App.Tests.Unit/ToolGroupItemTests.cs test/Capacitor.App.Tests.Unit/ChatTabViewModelTests.cs
git commit -m "Label kcap tool rows and attach cards to settled producers (#1098)" -m "A card is set before the outcome flips, so a folding group already knows to keep it." -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

### Task 5: The row label and the card in the chat view

**Files:**
- Modify: `src/Capacitor.App/Views/ChatTabView.axaml:151-181` (the `ToolCallItem` template)
- Modify: `src/Capacitor.App/Views/ChatTabView.axaml.cs` (a copy-link handler)
- Modify: `src/Capacitor.App/Views/ChatBubbleStyles.axaml` (card styles)
- Test: `test/Capacitor.App.Tests.Unit/ChatTabViewSmokeTests.cs`

**Interfaces:**
- Consumes: `ToolCallItem.Label`, `HasLabel`, `HasCard`, `Card`, `CanOpenCard`, `OpenCardCommand`.

- [ ] **Step 1: Write the failing smoke test**

In `test/Capacitor.App.Tests.Unit/ChatTabViewSmokeTests.cs` add two constants beside `ToolResultLine`:

```csharp
    const string PublishCallLine = """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_pub","name":"mcp__plugin_kcap_kcap-artefacts__publish_artefact","input":{"title":"Retention brief","html":"<p>x</p>"}}]}}""";
    const string PublishResultLine = """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"toolu_pub","content":"{\"artefact\":{\"artefact_id\":\"01ec\",\"title\":\"Retention brief\",\"owner_user_id\":\"u1\",\"visibility\":\"org\",\"latest_version\":1,\"updated_at\":\"2026-10-07T10:00:00Z\",\"is_owner\":true,\"url\":\"https://kurrent.kcap.ai/artefacts/01ec\"}}"}]}}""";
```

and this test, modelled on the suite's existing single-row tests (use the suite's `Host`, its `ShowAsync`/`Settle` helpers and `Find` by name exactly as its neighbours do):

```csharp
    /// A labelled row shows its verb phrase beside the detail; once the result lands the same item
    /// renders as a card with the page's name and its actions, and the plain row is gone.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_publish_row_turns_into_a_card_when_its_result_lands() {
        await RunOnUiAsync(async () => {
            var path = Tmp.CreateFile("t.jsonl", [PublishCallLine]);
            await using var host = await Host.ShowAsync(Tmp, path);

            var label = host.View.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "ToolRowLabel" && t.IsEffectivelyVisible);
            await Assert.That(label.Text).IsEqualTo("Published page");
            await Assert.That(host.View.GetVisualDescendants().OfType<Control>().Any(c => c.Name == "ToolCard" && c.IsEffectivelyVisible)).IsFalse();

            File.AppendAllText(path, PublishResultLine + "\n");
            await host.TickAsync();

            var card = host.View.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "ToolCard" && b.IsEffectivelyVisible);
            var title = host.View.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "ToolCardTitle");
            var name = host.View.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "ToolCardName");
            await Assert.That(title.Text).IsEqualTo("Published page");
            await Assert.That(name.Text).IsEqualTo("Retention brief");
            await Assert.That(host.View.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ToolCardOpen").IsEffectivelyVisible).IsTrue();
            await Assert.That(host.View.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ToolCardCopyLink").IsEffectivelyVisible).IsTrue();
            await Assert.That(ToolRows(host.View).Any(r => r.IsEffectivelyVisible)).IsFalse();
        });
    }
```

If the suite's `Host` factory has a different shape (`new Host(...)` plus an explicit show), follow the shape the neighbouring tests use; the assertions are what matter.

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/ChatTabViewSmokeTests/A_publish_row_turns_into_a_card_when_its_result_lands"`
Expected: FAIL, no control named `ToolRowLabel`.

- [ ] **Step 3: Replace the `ToolCallItem` template**

In `src/Capacitor.App/Views/ChatTabView.axaml` replace the whole `<DataTemplate x:DataType="vm:ToolCallItem">…</DataTemplate>` with:

```xml
                <DataTemplate x:DataType="vm:ToolCallItem">
                    <Panel>
                        <Grid x:Name="ToolCallRow" Margin="0,0,0,4" MinHeight="18" HorizontalAlignment="Stretch" IsVisible="{Binding !HasCard}">
                            <Grid.ColumnDefinitions>
                                <ColumnDefinition Width="Auto" />
                                <ColumnDefinition Width="*" MinWidth="0" />
                                <ColumnDefinition Width="Auto" />
                            </Grid.ColumnDefinitions>
                            <!-- The verb phrase of a kcap or foreign MCP row, muted so the detail stays the bright bit. -->
                            <TextBlock x:Name="ToolRowLabel" Grid.Column="0" Classes="toolLine" Text="{Binding Label}"
                                       Foreground="{StaticResource KcapMutedBrush}" Margin="0,0,8,0"
                                       IsVisible="{Binding HasLabel}" />
                            <SelectableTextBlock Grid.Column="1" Classes="toolLine" Classes.prose="{Binding DetailIsProse}"
                                       Text="{Binding Detail}" Foreground="{StaticResource KcapTextBrush}"
                                       IsVisible="{Binding HasDetail}" />
                            <!-- A bare name is fallback chrome only; a label already names the call, so the raw
                                 identifier never shows beside one. -->
                            <SelectableTextBlock Grid.Column="1" Classes="toolLine" Text="{Binding Name}"
                                       Foreground="{StaticResource KcapMutedBrush}">
                                <SelectableTextBlock.IsVisible>
                                    <MultiBinding Converter="{x:Static BoolConverters.And}">
                                        <Binding Path="!HasDetail" />
                                        <Binding Path="!HasLabel" />
                                    </MultiBinding>
                                </SelectableTextBlock.IsVisible>
                            </SelectableTextBlock>
                            <Panel Grid.Column="2">
                                <Border x:Name="ToolStatusPill" Classes="toolStatus" Width="14" Height="8"
                                        CornerRadius="4" Margin="16,0,0,0" VerticalAlignment="Center"
                                        IsVisible="{Binding IsSettled}"
                                        Background="{Binding IsError, Converter={x:Static views:ToolOutcomeBrushConverter.Instance}}" />
                                <Border Classes="toolRunning toolStatus" Width="14" Height="8"
                                        CornerRadius="4" Margin="16,0,0,0" VerticalAlignment="Center"
                                        IsVisible="{Binding IsRunning}" views:PulseClock.IsActive="{Binding IsRunning}"
                                        Background="{StaticResource KcapWarningBrush}" />
                                <TextBlock Text="?" FontSize="11" Margin="16,0,0,0"
                                           VerticalAlignment="Center"
                                           IsVisible="{Binding IsAwaitingPermission}"
                                           Foreground="{Binding IsError, Converter={x:Static views:ToolOutcomeBrushConverter.Instance}}" />
                            </Panel>
                        </Grid>
                        <Border x:Name="ToolCard" Classes="toolCard" IsVisible="{Binding HasCard}">
                            <Grid ColumnDefinitions="*,Auto" RowDefinitions="Auto,Auto" ColumnSpacing="12" RowSpacing="2">
                                <TextBlock x:Name="ToolCardTitle" Classes="toolLine" Text="{Binding Card.Title}" Foreground="{StaticResource KcapMutedBrush}" />
                                <TextBlock x:Name="ToolCardName" Grid.Row="1" Text="{Binding Card.Name}" FontWeight="SemiBold" FontSize="13.5"
                                           Foreground="{StaticResource KcapTextBrush}" TextTrimming="CharacterEllipsis" />
                                <TextBlock x:Name="ToolCardMeta" Grid.Row="1" Grid.Column="0" Margin="0,20,0,0" Classes="toolLine"
                                           Text="{Binding Card.Meta}" Foreground="{StaticResource KcapFaintBrush}" />
                                <StackPanel Grid.Column="1" Grid.RowSpan="2" Orientation="Horizontal" Spacing="6" VerticalAlignment="Center">
                                    <Button x:Name="ToolCardOpen" Content="Open" Classes="kcapGhost kcapQuiet"
                                            Command="{Binding OpenCardCommand}" IsVisible="{Binding CanOpenCard}" />
                                    <Button x:Name="ToolCardCopyLink" Content="Copy link" Classes="kcapGhost kcapQuiet"
                                            Tag="{Binding Card.Url}" Click="OnCopyLinkClick"
                                            IsVisible="{Binding Card.Url, Converter={x:Static StringConverters.IsNotNullOrEmpty}}" />
                                </StackPanel>
                            </Grid>
                        </Border>
                    </Panel>
                </DataTemplate>
```

If the meta line overlaps the name at runtime, give the card a three-row grid (title, name, meta) instead of the margin trick; the names and the visibility bindings are what the test pins.

- [ ] **Step 4: Style the card**

Append to `src/Capacitor.App/Views/ChatBubbleStyles.axaml` inside the root `<Styles>`:

```xml
    <!-- A producing call's card: raised against the group it sits in, padded like a pane card. -->
    <Style Selector="Border.toolCard">
        <Setter Property="Background" Value="{StaticResource KcapSurfaceRaisedBrush}" />
        <Setter Property="BorderBrush" Value="{StaticResource KcapBorderBrush}" />
        <Setter Property="BorderThickness" Value="1" />
        <Setter Property="CornerRadius" Value="8" />
        <Setter Property="Padding" Value="12,8" />
        <Setter Property="Margin" Value="0,2,0,6" />
    </Style>
```

- [ ] **Step 5: Add the copy handler**

In `src/Capacitor.App/Views/ChatTabView.axaml.cs`, beside `OnToolSummaryClick`:

```csharp
    async void OnCopyLinkClick(object? sender, RoutedEventArgs e) {
        if (sender is not Control { Tag: string { Length: > 0 } url } control) return;
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        await clipboard.SetTextAsync(url);
        ToolTip.SetTip(control, "Copied");
        ToolTip.SetIsOpen(control, true);
    }
```

Add `using Avalonia.Controls;` and `using Avalonia.Interactivity;` if the file lacks them.

- [ ] **Step 6: Run the smoke test and the full app suite**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/ChatTabViewSmokeTests/*"`
Expected: the new test passes; the existing `ToolCallRow` tests still find their rows (the name is unchanged and the row stays visible for every non-card call).

Then run the whole suite once: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj`. Expected: green. Also `dotnet build src/Capacitor.App/Capacitor.App.csproj 2>&1 | grep -E 'AVLN|warning' | head` must print nothing.

- [ ] **Step 7: Commit**

```bash
git add src/Capacitor.App/Views/ChatTabView.axaml src/Capacitor.App/Views/ChatTabView.axaml.cs src/Capacitor.App/Views/ChatBubbleStyles.axaml test/Capacitor.App.Tests.Unit/ChatTabViewSmokeTests.cs
git commit -m "Render kcap tool labels and producer cards in the chat (#1098)" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

## Slice 2: the Artefacts tab with documents

### Task 6: A totalized plan-artifacts read in Core

**Files:**
- Create: `src/Capacitor.Cli.Core/Plans/PlanArtifactsRead.cs`
- Create: `src/Capacitor.Cli.Core/Plans/PlanArtifactsClient.cs`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/Plans/PlanArtifactsClientTests.cs`

**Interfaces:**
- Consumes: `PlanArtifactsResponseDto`, `PlanArtifactDto` (`Capacitor.Cli.Core.Models`), `SessionPlansReadKind`, `CapacitorJsonContext.Default.PlanArtifactsResponseDto`, `WorkContextIds.CanonicalSessionId`.
- Produces: `record PlanArtifactsRead(SessionPlansReadKind Kind, PlanArtifactsResponseDto? Body)` with `static PlanArtifactsRead Of(SessionPlansReadKind kind)`; `PlanArtifactsClient(HttpClient http, string serverUrl)` with `Task<PlanArtifactsRead> ReadAsync(string sessionId, CancellationToken ct)`.

- [ ] **Step 1: Write the failing tests**

Create `test/Capacitor.Cli.Core.Tests.Unit/Plans/PlanArtifactsClientTests.cs`:

```csharp
using System.Net;
using System.Text;
using Capacitor.Cli.Core.Plans;

namespace Capacitor.Cli.Core.Tests.Unit.Plans;

/// The channel's totalization: which statuses mean signed out, unavailable or unreachable, and that
/// a good body parses into the DTO with its chain query on the URL.
public class PlanArtifactsClientTests {
    const string Session = "0123456789abcdef0123456789abcdef";
    const string Body = """
        {"primary":null,"artifacts":[{"artifact_id":"a1","kind":"design","title":"Design","source":"declared","session_id":"0123456789abcdef0123456789abcdef","path":"docs/x-design.md","content":"# Design","content_state":"ok","is_complete":true,"is_confirmed":true,"content_hash":"abc","version":1,"discovered_at":"2026-10-07T10:00:00Z","confidence":"high","reason":"declared","is_primary":true}],"diagnostics":[]}
        """;

    sealed class Handler(HttpStatusCode status, string body = "{}") : HttpMessageHandler {
        public Uri? Requested;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            Requested = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    static (PlanArtifactsClient Client, Handler Handler) Build(HttpStatusCode status, string body = "{}") {
        var handler = new Handler(status, body);
        return (new PlanArtifactsClient(new HttpClient(handler), "https://server.test/"), handler);
    }

    [Test]
    public async Task A_good_body_reads_ready_with_the_chain_query() {
        var (client, handler) = Build(HttpStatusCode.OK, Body);
        var read = await client.ReadAsync(Session, CancellationToken.None);
        await Assert.That(read.Kind).IsEqualTo(SessionPlansReadKind.Ready);
        await Assert.That(read.Body!.Artifacts.Single().Path).IsEqualTo("docs/x-design.md");
        await Assert.That(handler.Requested!.ToString()).IsEqualTo($"https://server.test/api/sessions/{Session}/plan-artifacts?chain=true");
    }

    [Test]
    [Arguments(HttpStatusCode.Unauthorized, SessionPlansReadKind.SignedOut)]
    [Arguments(HttpStatusCode.Forbidden, SessionPlansReadKind.Unavailable)]
    [Arguments(HttpStatusCode.NotFound, SessionPlansReadKind.Unavailable)]
    [Arguments(HttpStatusCode.BadGateway, SessionPlansReadKind.Unreachable)]
    public async Task Statuses_totalize(HttpStatusCode status, SessionPlansReadKind expected) {
        var (client, _) = Build(status);
        var read = await client.ReadAsync(Session, CancellationToken.None);
        await Assert.That(read.Kind).IsEqualTo(expected);
        await Assert.That(read.Body).IsNull();
    }

    [Test]
    public async Task A_malformed_session_id_is_unavailable_without_a_request() {
        var (client, handler) = Build(HttpStatusCode.OK, Body);
        var read = await client.ReadAsync("not-a-session", CancellationToken.None);
        await Assert.That(read.Kind).IsEqualTo(SessionPlansReadKind.Unavailable);
        await Assert.That(handler.Requested).IsNull();
    }

    [Test]
    public async Task The_callers_cancellation_propagates() {
        var (client, _) = Build(HttpStatusCode.OK, Body);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => client.ReadAsync(Session, cts.Token));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet build test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj 2>&1 | grep -E 'error' | head`
Expected: errors naming `PlanArtifactsClient` and `PlanArtifactsRead`.

- [ ] **Step 3: Create the record and the client**

`src/Capacitor.Cli.Core/Plans/PlanArtifactsRead.cs`:

```csharp
namespace Capacitor.Cli.Core.Plans;

/// One read of a session's plan artifacts, totalized the way the plans read is; Body rides only
/// with Ready.
public sealed record PlanArtifactsRead(SessionPlansReadKind Kind, PlanArtifactsResponseDto? Body) {
    public static PlanArtifactsRead Of(SessionPlansReadKind kind) => new(kind, null);
}
```

`src/Capacitor.Cli.Core/Plans/PlanArtifactsClient.cs`:

```csharp
using System.Net.Http.Json;
using System.Text.Json;
using Capacitor.Cli.Core.WorkItems;

namespace Capacitor.Cli.Core.Plans;

/// The HTTP channel for `plan-artifacts?chain=true`, the one plans route that carries document
/// bodies. <paramref name="http"/> must already carry the caller's bearer. Degrades rather than
/// throws, except for the caller's own cancellation, which propagates.
public sealed class PlanArtifactsClient(HttpClient http, string serverUrl) {
    readonly string _base = serverUrl.TrimEnd('/');

    public async Task<PlanArtifactsRead> ReadAsync(string sessionId, CancellationToken ct) {
        if (WorkContextIds.CanonicalSessionId(sessionId) is not { } id) return PlanArtifactsRead.Of(SessionPlansReadKind.Unavailable);

        try {
            using var req  = new HttpRequestMessage(HttpMethod.Get, $"{_base}/api/sessions/{Uri.EscapeDataString(id)}/plan-artifacts?chain=true");
            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);

            return (int)resp.StatusCode switch {
                401        => PlanArtifactsRead.Of(SessionPlansReadKind.SignedOut),
                403 or 404 => PlanArtifactsRead.Of(SessionPlansReadKind.Unavailable),
                >= 200 and < 300 when await resp.Content.ReadFromJsonAsync(CapacitorJsonContext.Default.PlanArtifactsResponseDto, ct).ConfigureAwait(false) is { } body
                    => new(SessionPlansReadKind.Ready, body),
                _ => PlanArtifactsRead.Of(SessionPlansReadKind.Unreachable),
            };
        } catch (Exception e) when (IsTransient(e, ct)) {
            return PlanArtifactsRead.Of(SessionPlansReadKind.Unreachable);
        }
    }

    static bool IsTransient(Exception e, CancellationToken ct) =>
        e is OperationCanceledException
            ? !ct.IsCancellationRequested
            : e is HttpRequestException or JsonException or NotSupportedException or IOException;
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter "/*/*/PlanArtifactsClientTests/*"`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Core/Plans/PlanArtifactsRead.cs src/Capacitor.Cli.Core/Plans/PlanArtifactsClient.cs test/Capacitor.Cli.Core.Tests.Unit/Plans/PlanArtifactsClientTests.cs
git commit -m "Read a session's plan artifacts as a totalized channel (#1098)" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

### Task 7: The app-side source for plan artifacts

**Files:**
- Create: `src/Capacitor.App/Services/IPlanArtifactSource.cs`
- Create: `src/Capacitor.App/Services/ServerPlanArtifactSource.cs`
- Modify: `src/Capacitor.App/Services/ServerClients.cs` (one more owned source)
- Modify: `src/Capacitor.App/App.axaml.cs:498-504`
- Create: `test/Capacitor.App.Tests.Unit/FakePlanArtifactSource.cs`
- Test: `test/Capacitor.App.Tests.Unit/ServerPlanArtifactSourceTests.cs`

**Interfaces:**
- Consumes: `PlanArtifactsClient`, `PlanArtifactsRead`, `AuthenticatedServerReads<T>`.
- Produces: `interface IPlanArtifactSource { Task<PlanArtifactsRead> ReadAsync(string sessionId, CancellationToken ct); }`; `ServerPlanArtifactSource : IPlanArtifactSource, IAsyncDisposable` with `InvalidateAuthentication()`; `ServerClients(..., IAsyncDisposable? plans = null, IAsyncDisposable? planArtifacts = null)`; test fake `FakePlanArtifactSource` with `Enqueue`, `Gate`, `Requested`, `Default`.

- [ ] **Step 1: Write the failing test**

Create `test/Capacitor.App.Tests.Unit/ServerPlanArtifactSourceTests.cs`:

```csharp
using System.Net;
using System.Text;
using Capacitor.App.Services;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Plans;

namespace Capacitor.App.Tests.Unit;

/// What the source itself decides: a missing sign-in reads signed out without a client, and a
/// 401 retires the client so the next read builds a fresh one. No network.
public class ServerPlanArtifactSourceTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    const string Session = "0123456789abcdef0123456789abcdef";

    sealed class ScriptedHandler(HttpStatusCode status) : HttpMessageHandler {
        public bool Disposed;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status) {
                Content = new StringContent("""{"primary":null,"artifacts":[],"diagnostics":[]}""", Encoding.UTF8, "application/json"),
            });

        protected override void Dispose(bool disposing) {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    static (ServerPlanArtifactSource Source, List<ScriptedHandler> Handlers) Build(ConfigRoot config, ProfileContext? profiles, Queue<HttpStatusCode> statuses) {
        var handlers = new List<ScriptedHandler>();
        var source = new ServerPlanArtifactSource(config, profiles, ProfileOverrides.None, MachineAuth.None, (_, _, _, _) => {
            var handler = new ScriptedHandler(statuses.Dequeue());
            handlers.Add(handler);
            return Task.FromResult((new HttpClient(handler), AuthStatus.Ok));
        });
        return (source, handlers);
    }

    [Test]
    public async Task A_null_profile_reads_signed_out_without_building_a_client() {
        var (source, handlers) = Build(Config.Root, profiles: null, new());

        var read = await source.ReadAsync(Session, CancellationToken.None);

        await Assert.That(read.Kind).IsEqualTo(SessionPlansReadKind.SignedOut);
        await Assert.That(handlers).IsEmpty();
        await source.DisposeAsync();
    }

    [Test]
    public async Task A_signed_out_read_retires_the_client_and_the_next_read_builds_a_new_one() {
        var (source, handlers) = Build(Config.Root, Resolutions.At("http://server.test", Config.Root), new([HttpStatusCode.Unauthorized, HttpStatusCode.OK]));

        var first = await source.ReadAsync(Session, CancellationToken.None);
        var second = await source.ReadAsync(Session, CancellationToken.None);

        await Assert.That(first.Kind).IsEqualTo(SessionPlansReadKind.SignedOut);
        await Assert.That(second.Kind).IsEqualTo(SessionPlansReadKind.Ready);
        await Assert.That(handlers.Count).IsEqualTo(2);
        await Assert.That(handlers[0].Disposed).IsTrue();
        await source.DisposeAsync();
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet build test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj 2>&1 | grep -E 'error' | head`
Expected: errors naming `ServerPlanArtifactSource`.

- [ ] **Step 3: Create the interface, the source and the fake**

`src/Capacitor.App/Services/IPlanArtifactSource.cs`:

```csharp
using Capacitor.Cli.Core.Plans;

namespace Capacitor.App.Services;

/// One read of a session's plan artifacts, bodies included, however the app reaches the server.
public interface IPlanArtifactSource {
    Task<PlanArtifactsRead> ReadAsync(string sessionId, CancellationToken ct);
}
```

`src/Capacitor.App/Services/ServerPlanArtifactSource.cs`:

```csharp
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Plans;

namespace Capacitor.App.Services;

public sealed class ServerPlanArtifactSource : IPlanArtifactSource, IAsyncDisposable {
    readonly AuthenticatedServerReads<PlanArtifactsClient> _reads;

    public ServerPlanArtifactSource(ConfigRoot config, ProfileContext? profiles, ProfileOverrides env,
            MachineAuth machine, AuthenticatedServerReads<PlanArtifactsClient>.ClientFactory? factory = null) {
        _reads = new(config, profiles, env, machine, (http, url) => new PlanArtifactsClient(http, url), factory);
    }

    public Task<PlanArtifactsRead> ReadAsync(string sessionId, CancellationToken ct) => _reads.ReadAsync(
        (channel, token) => channel.ReadAsync(sessionId, token), read => read.Kind == SessionPlansReadKind.SignedOut,
        PlanArtifactsRead.Of(SessionPlansReadKind.SignedOut), PlanArtifactsRead.Of(SessionPlansReadKind.Unreachable), ct);

    public ValueTask DisposeAsync() => _reads.DisposeAsync();
    public void InvalidateAuthentication() => _reads.Invalidate();
}
```

`test/Capacitor.App.Tests.Unit/FakePlanArtifactSource.cs`:

```csharp
using Capacitor.App.Services;
using Capacitor.Cli.Core.Plans;

namespace Capacitor.App.Tests.Unit;

/// Scripted IPlanArtifactSource: reads answer from a queue, or park on a gate so a test can settle
/// them in a chosen order. Every read records the id it was asked for.
sealed class FakePlanArtifactSource : IPlanArtifactSource {
    readonly Queue<PlanArtifactsRead> _scripted = new();
    readonly Queue<TaskCompletionSource<PlanArtifactsRead>> _gates = new();

    public readonly List<string> Requested = [];
    public PlanArtifactsRead Default = new(SessionPlansReadKind.Ready, new PlanArtifactsResponseDto());

    public void Enqueue(params PlanArtifactsRead[] reads) {
        foreach (var read in reads) _scripted.Enqueue(read);
    }

    /// The next read awaits the returned source instead of answering from the queue.
    public TaskCompletionSource<PlanArtifactsRead> Gate() {
        var gate = new TaskCompletionSource<PlanArtifactsRead>(TaskCreationOptions.RunContinuationsAsynchronously);
        _gates.Enqueue(gate);
        return gate;
    }

    public async Task<PlanArtifactsRead> ReadAsync(string sessionId, CancellationToken ct) {
        Requested.Add(sessionId);
        if (_gates.Count > 0) return await _gates.Dequeue().Task.WaitAsync(ct);
        await Task.Yield();
        return _scripted.Count > 0 ? _scripted.Dequeue() : Default;
    }
}
```

`PlanArtifactsResponseDto` lives in `Capacitor.Cli.Core`; add `using Capacitor.Cli.Core;` to the fake if the compiler asks.

- [ ] **Step 4: Own it in `ServerClients` and build it in the app**

In `src/Capacitor.App/Services/ServerClients.cs` the constructor becomes:

```csharp
    public ServerClients(IAsyncDisposable? launch, IAsyncDisposable? workContext, IAsyncDisposable? pullRequests = null,
            IAsyncDisposable? plans = null, IAsyncDisposable? planArtifacts = null) {
        _cleanup = new Lazy<Task>(() => CleanupAsync(launch, workContext, _signIn, pullRequests, plans, planArtifacts), LazyThreadSafetyMode.ExecutionAndPublication);
        _invalidateAuthentication = () => {
            (workContext as ServerWorkContextSource)?.InvalidateAuthentication();
            (pullRequests as ServerPullRequestSource)?.InvalidateAuthentication();
            (plans as ServerPlanSource)?.InvalidateAuthentication();
            (planArtifacts as ServerPlanArtifactSource)?.InvalidateAuthentication();
        };
    }
```

Give the static `CleanupAsync` a trailing `IAsyncDisposable? planArtifacts` parameter and dispose it at the point it disposes `plans`, with the same null guard.

In `src/Capacitor.App/App.axaml.cs`, after line 498 (`var plans = new ServerPlanSource(...)`):

```csharp
        var planArtifacts = new ServerPlanArtifactSource(_config, profiles, _serverEnv, _machineEnv);
```

and change the `ServerClients` construction to `new ServerClients(serverLane, workContext, pullRequests, plans, planArtifacts)`. Task 9 threads `planArtifacts` into the view models.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/ServerPlanArtifactSourceTests/*"` and `--treenode-filter "/*/*/ServerClientsTests/*"`.
Expected: both green.

- [ ] **Step 6: Commit**

```bash
git add src/Capacitor.App/Services/IPlanArtifactSource.cs src/Capacitor.App/Services/ServerPlanArtifactSource.cs src/Capacitor.App/Services/ServerClients.cs src/Capacitor.App/App.axaml.cs test/Capacitor.App.Tests.Unit/FakePlanArtifactSource.cs test/Capacitor.App.Tests.Unit/ServerPlanArtifactSourceTests.cs
git commit -m "Give the app an authenticated plan-artifacts source (#1098)" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

### Task 8: The Artefacts tab view model, its rows and the document reader

**Files:**
- Create: `src/Capacitor.App/ViewModels/DocumentRow.cs`
- Create: `src/Capacitor.App/ViewModels/DriftState.cs`
- Create: `src/Capacitor.App/ViewModels/DocumentReaderViewModel.cs`
- Create: `src/Capacitor.App/ViewModels/ArtefactsTabViewModel.cs`
- Test: `test/Capacitor.App.Tests.Unit/DocumentReaderViewModelTests.cs`, `test/Capacitor.App.Tests.Unit/ArtefactsTabViewModelTests.cs`

**Interfaces:**
- Consumes: `IPlanArtifactSource`, `PlanArtifactsRead`, `PlanArtifactDto`, `PlanActivity` (`PlanWritten`), `PlanSectionViewModel.SettleDelay`, `RelativeTime`, `StableRows`.
- Produces: `DocumentRow` (`Kind`, `Title`, `Path`, `FileName`, `Source`, `ContentState`, `Content`, `ContentHash`, `OriginalBytes`, `DiscoveredAt`, `IsPrimary`, `StateChip`, `HasStateChip`, `IsSelected`, `static DocumentRow From(PlanArtifactDto)`, `bool MatchesPath(string path)`); `enum DriftState { Unknown, Same, Changed, Missing }`; `DocumentReaderViewModel` (`KindLabel`, `FileName`, `Path`, `Body`, `HasBody`, `Notice`, `HasNotice`, `SizeLabel`, `DeclaredLabel`, `static DocumentReaderViewModel For(DocumentRow row, DriftState drift, DateTimeOffset now)`); `ArtefactsTabViewModel(IPlanArtifactSource? source, PlanActivity activity, TimeProvider time, Func<string, byte[]?>? readWorkingCopy = null)` with `Documents`, `HasAny`, `SummaryText`, `Selected`, `Reader`, `IsShown`, `SelectCommand`, `event Action? OpenRequested`, `void SwitchSession(string sessionId, string? root)`, `void Refresh()`, `bool OpenDocument(string path)`, `void RequestOpen()`, `Task TeardownAsync()`, `internal Task? PendingReadForTesting`.

- [ ] **Step 1: Write the failing reader tests**

Create `test/Capacitor.App.Tests.Unit/DocumentReaderViewModelTests.cs`:

```csharp
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core;

namespace Capacitor.App.Tests.Unit;

/// Pure: the reader is a projection of one row and one drift verdict.
public class DocumentReaderViewModelTests {
    static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    static PlanArtifactDto Dto(string kind = "design", string path = "docs/superpowers/specs/x-design.md", string? content = "# Design",
            string state = "ok", long? bytes = 2048, string source = "declared") => new() {
        ArtifactId = "a1", Kind = kind, Title = "Design", Source = source, SessionId = "s", Path = path, Content = content,
        ContentState = state, IsComplete = true, IsConfirmed = true, OriginalBytes = bytes, ContentHash = "abc", Version = 1,
        DiscoveredAt = Now.AddMinutes(-12), Confidence = "high", Reason = "declared", IsPrimary = true,
    };

    [Test]
    public async Task A_whole_declared_document_reads_with_its_header_and_no_notice() {
        var reader = DocumentReaderViewModel.For(DocumentRow.From(Dto()), DriftState.Same, Now);
        await Assert.That(reader.KindLabel).IsEqualTo("Design");
        await Assert.That(reader.FileName).IsEqualTo("x-design.md");
        await Assert.That(reader.Path).IsEqualTo("docs/superpowers/specs/x-design.md");
        await Assert.That(reader.Body).IsEqualTo("# Design");
        await Assert.That(reader.HasBody).IsTrue();
        await Assert.That(reader.HasNotice).IsFalse();
        await Assert.That(reader.SizeLabel).IsEqualTo("2 KB");
        await Assert.That(reader.DeclaredLabel).IsEqualTo("Declared 12 min ago");
    }

    [Test]
    public async Task Truncated_and_unavailable_bodies_say_so() {
        var truncated = DocumentReaderViewModel.For(DocumentRow.From(Dto(content: "# Desi", state: "truncated", bytes: 400_000)), DriftState.Same, Now);
        await Assert.That(truncated.HasBody).IsTrue();
        await Assert.That(truncated.Notice).IsEqualTo("Truncated: the server keeps the first 256 KB of a declared document.");

        var unavailable = DocumentReaderViewModel.For(DocumentRow.From(Dto(content: null, state: "unavailable")), DriftState.Unknown, Now);
        await Assert.That(unavailable.HasBody).IsFalse();
        await Assert.That(unavailable.Body).IsEqualTo("");
        await Assert.That(unavailable.Notice).IsEqualTo("No body is available: this document was declared by hash alone.");
    }

    [Test]
    public async Task Drift_is_named_when_the_body_is_whole() {
        await Assert.That(DocumentReaderViewModel.For(DocumentRow.From(Dto()), DriftState.Changed, Now).Notice)
            .IsEqualTo("Working copy has changed since this was declared.");
        await Assert.That(DocumentReaderViewModel.For(DocumentRow.From(Dto()), DriftState.Missing, Now).Notice)
            .IsEqualTo("Working copy is gone: nothing is at this path any more.");
        await Assert.That(DocumentReaderViewModel.For(DocumentRow.From(Dto()), DriftState.Unknown, Now).HasNotice).IsFalse();
    }

    [Test]
    [Arguments("plan", "Plan")]
    [Arguments("spec", "Spec")]
    [Arguments("design", "Design")]
    [Arguments("checklist", "Document")]
    public async Task Kind_labels(string kind, string label) {
        await Assert.That(DocumentReaderViewModel.For(DocumentRow.From(Dto(kind: kind)), DriftState.Unknown, Now).KindLabel).IsEqualTo(label);
    }

    [Test]
    public async Task A_row_cuts_its_file_name_on_either_separator_and_matches_by_suffix() {
        var row = DocumentRow.From(Dto(path: @"docs\plans\a.md"));
        await Assert.That(row.FileName).IsEqualTo("a.md");
        await Assert.That(row.MatchesPath("docs/plans/a.md")).IsTrue();
        await Assert.That(row.MatchesPath("/Users/me/repo/docs/plans/a.md")).IsTrue();
        await Assert.That(row.MatchesPath("a.md")).IsTrue();
        await Assert.That(row.MatchesPath("docs/plans/b.md")).IsFalse();
        await Assert.That(row.MatchesPath("xa.md")).IsFalse();
    }
}
```

`RelativeTime` is the app's existing helper; if "12 min ago" is not its exact spelling for twelve minutes, use the spelling `RelativeTimeTests` pins.

- [ ] **Step 2: Write the failing tab tests**

Create `test/Capacitor.App.Tests.Unit/ArtefactsTabViewModelTests.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Plans;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions.Enums;
using static Capacitor.App.Tests.Unit.AvaloniaSession;

namespace Capacitor.App.Tests.Unit;

/// The tab's list and reader: what a read lists, in what order, which document is open, how a row
/// is found from a path, and the lease that keeps a stale read from applying. Every read settles
/// through Dispatcher.UIThread, so every test runs under RunOnUiAsync and carries
/// [NotInParallel("AvaloniaSession")].
public class ArtefactsTabViewModelTests {
    const string SessionA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    const string SessionB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    sealed class Harness {
        public FakePlanArtifactSource Source { get; } = new();
        public FakeTimeProvider Time { get; } = new();
        public PlanActivity Activity { get; } = new();
        public Dictionary<string, byte[]> Disk { get; } = new(StringComparer.Ordinal);
        public ArtefactsTabViewModel Vm { get; }

        public Harness() => Vm = new ArtefactsTabViewModel(Source, Activity, Time, path => Disk.TryGetValue(path, out var bytes) ? bytes : null);

        public async Task SwitchAsync(string sessionId, string? root = "/repo") {
            Vm.SwitchSession(sessionId, root);
            await SettledAsync();
        }

        public Task SettledAsync() => Vm.PendingReadForTesting ?? Task.CompletedTask;

        public void WritePlan(string callId = "c1") => Activity.Apply([new ChatProjectionResult([
            new AcpEventEnvelope(Kind: AcpEventKind.ToolCall, ToolCallId: callId, ToolName: PlanToolNames.DeclareDocument),
            new AcpEventEnvelope(Kind: AcpEventKind.ToolResult, ToolCallId: callId),
        ], [], [])]);
    }

    static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    static PlanArtifactDto Doc(string id, string kind, string path, string content = "# x", string? hash = null, bool primary = false,
            string source = "declared", string state = "ok") => new() {
        ArtifactId = id, Kind = kind, Title = path, Source = source, SessionId = SessionA, Path = path, Content = content,
        ContentState = state, IsComplete = true, IsConfirmed = true, ContentHash = hash ?? Sha(content), Version = 1,
        DiscoveredAt = DateTimeOffset.UnixEpoch, Confidence = "high", Reason = source, IsPrimary = primary,
    };

    static PlanArtifactsRead Ready(params PlanArtifactDto[] docs) =>
        new(SessionPlansReadKind.Ready, new PlanArtifactsResponseDto { Artifacts = [.. docs] });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_read_lists_documents_plan_spec_design_other_primary_first_and_deduped_by_hash_then_path() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            await Assert.That(h.Vm.HasAny).IsFalse();

            h.Source.Enqueue(Ready(
                Doc("1", "design", "docs/d.md", "# d"),
                Doc("2", "plan", "docs/p2.md", "# p2"),
                Doc("3", "plan", "docs/p1.md", "# p1", primary: true),
                Doc("4", "design", "docs/d.md", "# d"),
                Doc("5", "checklist", "todo.md", "- [ ] x"),
                Doc("6", "spec", "docs/s.md", "# s", source: "repo_file"),
                Doc("7", "spec", "docs/s.md", "# s", source: "declared")));
            await h.SwitchAsync(SessionA);

            await Assert.That(h.Source.Requested).IsEquivalentTo(new[] { SessionA });
            await Assert.That(h.Vm.HasAny).IsTrue();
            await Assert.That(h.Vm.SummaryText).IsEqualTo("4 documents");
            await Assert.That(h.Vm.Documents.Select(d => d.Path))
                .IsEquivalentTo(new[] { "docs/p1.md", "docs/p2.md", "docs/s.md", "docs/d.md" }, CollectionOrdering.Matching);
            await Assert.That(h.Vm.Documents[2].Source).IsEqualTo("declared");
            await h.Vm.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Selecting_a_row_builds_the_reader_and_marks_it_and_a_path_opens_its_row_by_suffix() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            var opens = 0;
            h.Vm.OpenRequested += () => opens++;
            h.Source.Enqueue(Ready(Doc("1", "plan", "docs/p.md", "# p"), Doc("2", "design", @"docs\d.md", "# d")));
            await h.SwitchAsync(SessionA);
            await Assert.That(h.Vm.Selected).IsNull();
            await Assert.That(h.Vm.Reader).IsNull();

            await h.Vm.SelectCommand.Execute(h.Vm.Documents[0]);
            await Assert.That(h.Vm.Selected).IsSameReferenceAs(h.Vm.Documents[0]);
            await Assert.That(h.Vm.Documents[0].IsSelected).IsTrue();
            await Assert.That(h.Vm.Reader!.Body).IsEqualTo("# p");
            await Assert.That(opens).IsEqualTo(0);

            await Assert.That(h.Vm.OpenDocument("/Users/me/repo/docs/d.md")).IsTrue();
            await Assert.That(h.Vm.Selected!.Path).IsEqualTo(@"docs\d.md");
            await Assert.That(h.Vm.Documents[0].IsSelected).IsFalse();
            await Assert.That(opens).IsEqualTo(1);

            await Assert.That(h.Vm.OpenDocument("docs/nope.md")).IsFalse();
            await Assert.That(opens).IsEqualTo(1);
            await h.Vm.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Drift_compares_the_working_copy_hash_for_a_declared_document_only() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            h.Disk["/repo/docs/same.md"] = Encoding.UTF8.GetBytes("# same");
            h.Disk["/repo/docs/changed.md"] = Encoding.UTF8.GetBytes("# changed on disk");
            h.Source.Enqueue(Ready(
                Doc("1", "plan", "docs/same.md", "# same"),
                Doc("2", "spec", "docs/changed.md", "# changed"),
                Doc("3", "design", "docs/gone.md", "# gone"),
                Doc("4", "design", "docs/found.md", "# found", source: "repo_file")));
            await h.SwitchAsync(SessionA, root: "/repo");

            await h.Vm.SelectCommand.Execute(h.Vm.Documents[0]);
            await Assert.That(h.Vm.Reader!.HasNotice).IsFalse();
            await h.Vm.SelectCommand.Execute(h.Vm.Documents[1]);
            await Assert.That(h.Vm.Reader!.Notice).IsEqualTo("Working copy has changed since this was declared.");
            await h.Vm.SelectCommand.Execute(h.Vm.Documents[2]);
            await Assert.That(h.Vm.Reader!.Notice).IsEqualTo("Working copy is gone: nothing is at this path any more.");
            await h.Vm.SelectCommand.Execute(h.Vm.Documents[3]);
            await Assert.That(h.Vm.Reader!.HasNotice).IsFalse();
            await h.Vm.TeardownAsync();
        });
    }

    /// A remote session has no root; the reader then says nothing about drift.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Without_a_root_there_is_no_drift_verdict() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            h.Source.Enqueue(Ready(Doc("1", "plan", "docs/p.md", "# p")));
            await h.SwitchAsync(SessionA, root: null);
            await h.Vm.SelectCommand.Execute(h.Vm.Documents[0]);
            await Assert.That(h.Vm.Reader!.HasNotice).IsFalse();
            await h.Vm.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_write_in_the_transcript_reads_at_once_and_again_after_the_settle_delay() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            await h.SwitchAsync(SessionA);
            await Assert.That(h.Source.Requested.Count).IsEqualTo(1);

            h.WritePlan();
            await h.SettledAsync();
            await Assert.That(h.Source.Requested.Count).IsEqualTo(2);

            h.Time.Advance(PlanSectionViewModel.SettleDelay);
            await h.SettledAsync();
            await Assert.That(h.Source.Requested.Count).IsEqualTo(3);
            await h.Vm.TeardownAsync();
        });
    }

    /// The read in flight belongs to the session that started it: when the session changes before
    /// it lands, its rows never show, and a new session with nothing clears the list.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_stale_read_never_applies_and_an_empty_session_clears_the_list() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            var gate = h.Source.Gate();
            h.Vm.SwitchSession(SessionA, "/repo");
            var stale = h.Vm.PendingReadForTesting!;

            h.Source.Enqueue(Ready());
            await h.SwitchAsync(SessionB);
            gate.SetResult(Ready(Doc("1", "plan", "docs/p.md")));
            await stale;

            await Assert.That(h.Vm.HasAny).IsFalse();
            await Assert.That(h.Vm.Documents).IsEmpty();
            await Assert.That(h.Vm.Selected).IsNull();
            await h.Vm.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_refresh_keeps_the_selection_when_the_document_is_still_listed() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            h.Source.Enqueue(Ready(Doc("1", "plan", "docs/p.md", "# p")));
            await h.SwitchAsync(SessionA);
            await h.Vm.SelectCommand.Execute(h.Vm.Documents[0]);

            h.Source.Enqueue(Ready(Doc("1", "plan", "docs/p.md", "# p v2"), Doc("2", "spec", "docs/s.md")));
            h.Vm.Refresh();
            await h.SettledAsync();

            await Assert.That(h.Vm.Selected!.Path).IsEqualTo("docs/p.md");
            await Assert.That(h.Vm.Reader!.Body).IsEqualTo("# p v2");
            await Assert.That(h.Vm.Documents.Count).IsEqualTo(2);
            await h.Vm.TeardownAsync();
        });
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet build test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj 2>&1 | grep -E 'error' | head`
Expected: errors naming `ArtefactsTabViewModel`, `DocumentRow`, `DocumentReaderViewModel`, `DriftState`.

- [ ] **Step 4: Create the row, the drift enum and the reader**

`src/Capacitor.App/ViewModels/DriftState.cs`:

```csharp
namespace Capacitor.App.ViewModels;

/// How the working copy at a declared document's path compares with the declared snapshot.
/// Unknown when there is no root to look under, or the document was not declared.
public enum DriftState { Unknown, Same, Changed, Missing }
```

`src/Capacitor.App/ViewModels/DocumentRow.cs`:

```csharp
using Capacitor.Cli.Core;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// One document in the Artefacts tab. `Path` is the declaring machine's, relative to its repository
/// root, so the file name is cut on either separator and a path is matched by its tail.
public sealed class DocumentRow(string kind, string title, string path, string source, string contentState, string? content,
        string contentHash, long? originalBytes, DateTimeOffset discoveredAt, bool isPrimary) : ReactiveObject {
    public string Kind { get; } = kind;
    public string Title { get; } = title;
    public string Path { get; } = path;
    public string FileName { get; } = ToolCards.FileName(path);
    public string Source { get; } = source;
    public string ContentState { get; } = contentState;
    public string? Content { get; } = content;
    public string ContentHash { get; } = contentHash;
    public long? OriginalBytes { get; } = originalBytes;
    public DateTimeOffset DiscoveredAt { get; } = discoveredAt;
    public bool IsPrimary { get; } = isPrimary;

    /// "truncated" or "unavailable" beside the name; nothing for a whole body.
    public string StateChip => ContentState == "ok" ? "" : ContentState;
    public bool HasStateChip => ContentState != "ok";

    bool _isSelected;
    public bool IsSelected { get => _isSelected; internal set => this.RaiseAndSetIfChanged(ref _isSelected, value); }

    public static DocumentRow From(PlanArtifactDto dto) => new(
        dto.Kind, dto.Title, dto.Path ?? dto.Title, dto.Source, dto.ContentState, dto.Content,
        dto.ContentHash, dto.OriginalBytes, dto.DiscoveredAt, dto.IsPrimary);

    /// True when the other path ends with this row's path, on a segment boundary, whichever
    /// separator either side uses.
    public bool MatchesPath(string other) {
        var mine = Normalise(Path);
        var theirs = Normalise(other);
        if (theirs == mine) return true;
        return theirs.EndsWith("/" + mine, StringComparison.Ordinal) || mine.EndsWith("/" + theirs, StringComparison.Ordinal);
    }

    static string Normalise(string path) => path.Replace('\\', '/').TrimEnd('/');
}
```

`src/Capacitor.App/ViewModels/DocumentReaderViewModel.cs`:

```csharp
namespace Capacitor.App.ViewModels;

/// What the reader shows for one document: a projection built once per selection.
public sealed class DocumentReaderViewModel {
    const long Kilobyte = 1024;

    public required string KindLabel { get; init; }
    public required string FileName { get; init; }
    public required string Path { get; init; }
    public required string Body { get; init; }
    public bool HasBody => Body.Length > 0;
    public string? Notice { get; init; }
    public bool HasNotice => !string.IsNullOrEmpty(Notice);
    public required string SizeLabel { get; init; }
    public required string DeclaredLabel { get; init; }

    public static DocumentReaderViewModel For(DocumentRow row, DriftState drift, DateTimeOffset now) => new() {
        KindLabel = row.Kind switch { "plan" => "Plan", "spec" => "Spec", "design" => "Design", _ => "Document" },
        FileName = row.FileName,
        Path = row.Path,
        Body = row.Content ?? "",
        Notice = NoticeFor(row, drift),
        SizeLabel = row.OriginalBytes is { } bytes ? $"{Math.Max(1, bytes / Kilobyte)} KB" : "",
        DeclaredLabel = $"Declared {RelativeTime.Format(row.DiscoveredAt, now)}",
    };

    /// A body problem outranks drift: a reader who cannot see the text has no use for a comparison.
    static string? NoticeFor(DocumentRow row, DriftState drift) => row.ContentState switch {
        "truncated"   => "Truncated: the server keeps the first 256 KB of a declared document.",
        "unavailable" => "No body is available: this document was declared by hash alone.",
        _ => drift switch {
            DriftState.Changed => "Working copy has changed since this was declared.",
            DriftState.Missing => "Working copy is gone: nothing is at this path any more.",
            _ => null,
        },
    };
}
```

Use `RelativeTime`'s actual static method name and signature from `ViewModels/RelativeTime.cs` (it formats an instant against a now). If it has no two-argument form, pass the `TimeProvider` through `For` instead of a `DateTimeOffset`.

- [ ] **Step 5: Create the tab view model**

`src/Capacitor.App/ViewModels/ArtefactsTabViewModel.cs`:

```csharp
using System.Reactive;
using System.Reactive.Concurrency;
using System.Security.Cryptography;
using Avalonia.Collections;
using Avalonia.Threading;
using Capacitor.App.Services;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Plans;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// The Artefacts tab: the documents a session declared or wrote, read from plan-artifacts with
/// their bodies, and the one that is open. The session id is the read's identity; a lease owns one
/// id, its root, its cancellation and its pending read, a result applies only for the current lease,
/// and every lease transition happens on the UI thread.
public sealed class ArtefactsTabViewModel : ReactiveObject {
    sealed class ReadLease(string sessionId, string? root) {
        public string SessionId { get; } = sessionId;
        public string? Root { get; } = root;
        public CancellationTokenSource Cts { get; } = new();
        public Task? Pending;
        public bool RefreshPending;
        public bool IsReading => Pending is { IsCompleted: false };
    }

    readonly IPlanArtifactSource? _source;
    readonly PlanActivity _activity;
    readonly TimeProvider _time;
    readonly Func<string, byte[]?> _readWorkingCopy;
    readonly AvaloniaList<DocumentRow> _documents = [];
    readonly List<ReadLease> _outstanding = [];
    readonly ITimer _settle;
    ReadLease? _current;
    bool _tornDown;

    public IAvaloniaReadOnlyList<DocumentRow> Documents => _documents;
    public bool HasAny => _documents.Count > 0;
    public string SummaryText => _documents.Count == 1 ? "1 document" : $"{_documents.Count} documents";

    DocumentRow? _selected;
    public DocumentRow? Selected { get => _selected; private set => this.RaiseAndSetIfChanged(ref _selected, value); }

    DocumentReaderViewModel? _reader;
    public DocumentReaderViewModel? Reader { get => _reader; private set => this.RaiseAndSetIfChanged(ref _reader, value); }

    bool _isShown;
    /// Set by the workspace while the tab is the active one; the pane's poll refreshes only then.
    public bool IsShown { get => _isShown; set => this.RaiseAndSetIfChanged(ref _isShown, value); }

    public ReactiveCommand<DocumentRow, Unit> SelectCommand { get; }
    /// Something asked for the tab: a card's Open, a pane row, the pane summary.
    public event Action? OpenRequested;

    /// Test-only seam: the current lease's read, or the last one started.
    internal Task? PendingReadForTesting => _current?.Pending ?? _outstanding.LastOrDefault()?.Pending;

    public ArtefactsTabViewModel(IPlanArtifactSource? source, PlanActivity activity, TimeProvider time, Func<string, byte[]?>? readWorkingCopy = null) {
        _source = source;
        _activity = activity;
        _time = time;
        _readWorkingCopy = readWorkingCopy ?? ReadFile;
        SelectCommand = ReactiveCommand.Create<DocumentRow>(Select);
        _settle = time.CreateTimer(_ => RxSchedulers.MainThreadScheduler.Schedule(Refresh), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _activity.PlanWritten += OnPlanWritten;
    }

    public void SwitchSession(string sessionId, string? root) {
        if (_tornDown || _source is null) return;
        var old = _current;
        _current = new ReadLease(sessionId, root);
        old?.Cts.Cancel();
        Clear();
        StartRead(_current);
    }

    /// Reads now, or queues one follow-up behind the read in flight.
    public void Refresh() {
        if (_tornDown || _current is not { } lease) return;
        if (lease.IsReading) lease.RefreshPending = true;
        else StartRead(lease);
    }

    public void RequestOpen() => OpenRequested?.Invoke();

    /// Selects the row whose path matches and asks for the tab; false when no row does.
    public bool OpenDocument(string path) {
        var row = _documents.FirstOrDefault(d => d.MatchesPath(path));
        if (row is null) return false;
        Select(row);
        RequestOpen();
        return true;
    }

    void Select(DocumentRow row) {
        foreach (var document in _documents) document.IsSelected = ReferenceEquals(document, row);
        Selected = row;
        Reader = DocumentReaderViewModel.For(row, Drift(row), _time.GetUtcNow());
    }

    DriftState Drift(DocumentRow row) {
        if (_current?.Root is not { Length: > 0 } root || row.Source != "declared") return DriftState.Unknown;
        var full = System.IO.Path.Combine(root, row.Path.Replace('\\', '/'));
        byte[]? bytes;
        try { bytes = _readWorkingCopy(full); } catch (Exception) { return DriftState.Unknown; }
        if (bytes is null) return DriftState.Missing;
        return Convert.ToHexStringLower(SHA256.HashData(bytes)) == row.ContentHash ? DriftState.Same : DriftState.Changed;
    }

    void OnPlanWritten() {
        if (_tornDown) return;
        Refresh();
        _settle.Change(PlanSectionViewModel.SettleDelay, Timeout.InfiniteTimeSpan);
    }

    void StartRead(ReadLease lease) {
        lease.RefreshPending = false;
        lease.Pending = RunReadAsync(lease);
        _outstanding.Add(lease);
    }

    async Task RunReadAsync(ReadLease lease) {
        PlanArtifactsRead? read = null;
        try {
            read = await _source!.ReadAsync(lease.SessionId, lease.Cts.Token).ConfigureAwait(false);
        } catch (OperationCanceledException) {
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: artefacts tab: {ex.Message}");
            read = PlanArtifactsRead.Of(SessionPlansReadKind.Unreachable);
        }
        try {
            await Dispatcher.UIThread.InvokeAsync(() => Settle(lease, read));
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: artefacts tab: {ex.Message}");
        }
    }

    void Settle(ReadLease lease, PlanArtifactsRead? read) {
        _outstanding.Remove(lease);
        var current = ReferenceEquals(lease, _current) && !_tornDown;
        if (!current) { lease.Cts.Dispose(); return; }
        try {
            if (read is not null) Apply(read);
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: artefacts tab: {ex.Message}");
        }
        if (lease.RefreshPending) StartRead(lease);
    }

    void Apply(PlanArtifactsRead read) {
        switch (read.Kind) {
            case SessionPlansReadKind.Ready:
                Show(read.Body?.Artifacts ?? []);
                return;
            case SessionPlansReadKind.Unreachable:
                return;
            default:
                Clear();
                return;
        }
    }

    static int KindRank(string kind) => kind switch { "plan" => 0, "spec" => 1, "design" => 2, _ => 3 };

    /// Checklists are task lists, not documents. Two entries for one text (a declaration and the
    /// transcript's reconstruction) collapse to one, the declared one first.
    void Show(IReadOnlyList<PlanArtifactDto> artifacts) {
        var rows = new List<DocumentRow>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dto in artifacts
                     .Where(a => a.Kind != "checklist")
                     .OrderBy(a => a.Source == "declared" ? 0 : 1)) {
            var key = dto.ContentHash is { Length: > 0 } hash ? "h:" + hash : "p:" + (dto.Path ?? dto.Title);
            if (!seen.Add(key) || !seen.Add("p:" + (dto.Path ?? dto.Title))) continue;
            rows.Add(DocumentRow.From(dto));
        }
        var ordered = rows.OrderBy(r => KindRank(r.Kind)).ThenBy(r => r.IsPrimary ? 0 : 1).ThenBy(r => r.FileName, StringComparer.Ordinal).ToList();

        var openPath = Selected?.Path;
        _documents.Clear();
        _documents.AddRange(ordered);
        RaiseShape();

        var reopened = openPath is null ? null : _documents.FirstOrDefault(d => d.Path == openPath);
        if (reopened is not null) Select(reopened);
        else { Selected = null; Reader = null; }
    }

    void Clear() {
        Selected = null;
        Reader = null;
        if (_documents.Count == 0) return;
        _documents.Clear();
        RaiseShape();
    }

    void RaiseShape() {
        this.RaisePropertyChanged(nameof(HasAny));
        this.RaisePropertyChanged(nameof(SummaryText));
    }

    /// Shared-read so an agent still writing the file on Windows is not refused.
    static byte[]? ReadFile(string path) {
        if (!File.Exists(path)) return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    public async Task TeardownAsync() {
        if (_tornDown) return;
        _tornDown = true;
        _activity.PlanWritten -= OnPlanWritten;
        _settle.Dispose();
        SelectCommand.Dispose();
        var leases = _outstanding.ToArray();
        foreach (var lease in leases) lease.Cts.Cancel();
        _current = null;
        foreach (var lease in leases)
            if (lease.Pending is { } pending) {
                try { await pending; } catch (Exception) { }
            }
        foreach (var lease in leases) lease.Cts.Dispose();
    }
}
```

The dedupe in `Show` reads: a dto is kept when both its hash key and its path key are new; the `seen.Add(...)` pair relies on `||` short-circuiting, so an entry whose hash was seen is dropped before its path is recorded. The test's second `docs/s.md` (declared) sorts before the `repo_file` one and wins.

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/DocumentReaderViewModelTests/*"` and `--treenode-filter "/*/*/ArtefactsTabViewModelTests/*"`.
Expected: all pass. If `A_stale_read_never_applies…` hangs, the gate's `WaitAsync(ct)` did not observe the cancelled lease: check that `SwitchSession` cancels the old lease before `StartRead` of the new one.

- [ ] **Step 7: Commit**

```bash
git add src/Capacitor.App/ViewModels/DocumentRow.cs src/Capacitor.App/ViewModels/DriftState.cs src/Capacitor.App/ViewModels/DocumentReaderViewModel.cs src/Capacitor.App/ViewModels/ArtefactsTabViewModel.cs test/Capacitor.App.Tests.Unit/DocumentReaderViewModelTests.cs test/Capacitor.App.Tests.Unit/ArtefactsTabViewModelTests.cs
git commit -m "Model the Artefacts tab over a session's plan artifacts (#1098)" -m "The drift check hashes the working copy once per open and only for a declared document: the server's hash for a reconstructed file covers the logical text, not the bytes on disk." -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

### Task 9: The Artefacts tab on both lanes

**Files:**
- Modify: `src/Capacitor.App/ViewModels/WorkspaceViewModel.cs:13,60-80,96-145,215-230`
- Modify: `src/Capacitor.App/ViewModels/WorkContextViewModel.cs:243-270,325-334,372-376,418-432`
- Modify: `src/Capacitor.App/ViewModels/RemoteSessionViewModel.cs:14,47-65,125-131,142-215,277-281`
- Modify: `src/Capacitor.App/App.axaml.cs:597-614` (thread `planArtifacts` into both builders)
- Test: `test/Capacitor.App.Tests.Unit/WorkspaceViewModelTests.cs`, `test/Capacitor.App.Tests.Unit/RemoteSessionViewModelTests.cs`

**Interfaces:**
- Consumes: `ArtefactsTabViewModel`, `IPlanArtifactSource`, `ToolCard`, `ChatTabViewModel(..., openCard:)`.
- Produces: `WorkspaceTab.Artefacts`, `RemoteTab.Artefacts`; on both view models `ArtefactsTabViewModel Artefacts`, `bool ShowsArtefactsTab`, `bool IsArtefactsActive`, `ReactiveCommand<Unit, Unit> ShowArtefactsCommand`; `WorkspaceViewModel(..., IPlanSource? plans = null, IPlanArtifactSource? planArtifacts = null)`; `RemoteSessionViewModel(..., Func<ITerminalSurface>? surfaceFactory = null, IPlanArtifactSource? planArtifacts = null)`; `WorkContextViewModel(..., PlanActivity? planActivity = null, ArtefactsTabViewModel? artefacts = null)` exposing `ArtefactsTabViewModel? Artefacts` and `ReactiveCommand<Unit, Unit> OpenArtefactsCommand`.

- [ ] **Step 1: Write the failing workspace tests**

In `test/Capacitor.App.Tests.Unit/WorkspaceViewModelTests.cs` give `Build` an `IPlanArtifactSource? planArtifacts = null` parameter passed as `planArtifacts: planArtifacts`, add `using Capacitor.Cli.Core;` and `using Capacitor.Cli.Core.Plans;`, and add:

```csharp
    const string Session = "0123456789abcdef0123456789abcdef";

    static PlanArtifactDto Doc(string path, string kind = "design") => new() {
        ArtifactId = path, Kind = kind, Title = path, Source = "declared", SessionId = Session, Path = path, Content = "# x",
        ContentState = "ok", IsComplete = true, IsConfirmed = true, ContentHash = "h", Version = 1,
        DiscoveredAt = DateTimeOffset.UnixEpoch, Confidence = "high", Reason = "declared", IsPrimary = true,
    };

    static PlanArtifactsRead Ready(params PlanArtifactDto[] docs) =>
        new(SessionPlansReadKind.Ready, new PlanArtifactsResponseDto { Artifacts = [.. docs] });

    /// The tab exists only while the session has a document, like Pull request with its changes.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_artefacts_tab_appears_with_the_first_document_and_falls_back_to_chat_when_none_remain() {
        await RunOnUiAsync(async () => {
            var daemon = new FakeDaemonClientService();
            var source = new FakePlanArtifactSource();
            source.Enqueue(Ready(Doc("docs/x-design.md")));
            var vm = Build(daemon, NewActions(new ScriptedLocalControlOps(), new RecordingNotifier(), new RecordingOpener()), new FakeTerminalAttachClientFactory(), new FakeTimeProvider(), planArtifacts: source);
            await Assert.That(vm.ShowsArtefactsTab).IsFalse();

            daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: true, repoPath: "/repo/x", sessionId: Session));
            await (vm.Terminal.PendingResolveWorkForTesting ?? Task.CompletedTask);
            await (vm.Artefacts.PendingReadForTesting ?? Task.CompletedTask);
            await Assert.That(vm.ShowsArtefactsTab).IsTrue();
            await Assert.That(vm.ShowsSurfaceSwitch).IsTrue();

            await vm.ShowArtefactsCommand.Execute();
            await Assert.That(vm.IsArtefactsActive).IsTrue();
            await Assert.That(vm.Artefacts.IsShown).IsTrue();

            source.Enqueue(Ready());
            vm.Artefacts.Refresh();
            await (vm.Artefacts.PendingReadForTesting ?? Task.CompletedTask);
            await Assert.That(vm.ShowsArtefactsTab).IsFalse();
            await Assert.That(vm.ActiveTab).IsEqualTo(WorkspaceTab.Chat);
            await Assert.That(vm.Artefacts.IsShown).IsFalse();
            await vm.TeardownAsync();
        });
    }

    /// A card's Open: a document goes to the tab, a page to the browser.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_card_opens_a_document_in_the_tab_and_a_page_in_the_browser() {
        await RunOnUiAsync(async () => {
            var daemon = new FakeDaemonClientService();
            var source = new FakePlanArtifactSource();
            source.Enqueue(Ready(Doc("docs/x-design.md")));
            var opener = new RecordingOpener();
            var vm = Build(daemon, NewActions(new ScriptedLocalControlOps(), new RecordingNotifier(), opener), new FakeTerminalAttachClientFactory(), new FakeTimeProvider(), planArtifacts: source, opener: opener);
            daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: true, repoPath: "/repo/x", sessionId: Session));
            await (vm.Terminal.PendingResolveWorkForTesting ?? Task.CompletedTask);
            await (vm.Artefacts.PendingReadForTesting ?? Task.CompletedTask);

            vm.OpenCard(new ToolCard(ToolCardKind.Document, "Declared design doc", "x-design.md", "docs/x-design.md", null, "/repo/x/docs/x-design.md"));
            await Assert.That(vm.ActiveTab).IsEqualTo(WorkspaceTab.Artefacts);
            await Assert.That(vm.Artefacts.Selected!.Path).IsEqualTo("docs/x-design.md");

            vm.OpenCard(new ToolCard(ToolCardKind.Page, "Published page", "Brief", "v1 · Org", "https://x/artefacts/1", null));
            await Assert.That(opener.Opened).IsEquivalentTo(new[] { "https://x/artefacts/1" });
            await vm.TeardownAsync();
        });
    }
```

`Build` also gains `IUrlOpener? opener = null`, passed where it currently constructs `new RecordingOpener()`. `OpenCard` is the method the workspace hands to the chat as `openCard`; it is `internal` so the test can call it without a transcript.

- [ ] **Step 2: Write the failing remote test**

In `test/Capacitor.App.Tests.Unit/RemoteSessionViewModelTests.cs`, using the suite's existing builder for a `RemoteSessionViewModel` (give it an `IPlanArtifactSource? planArtifacts = null` parameter passed as `planArtifacts:`), add:

```csharp
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_remote_session_with_a_document_shows_the_artefacts_tab_without_a_pane() {
        await RunOnUiAsync(async () => {
            var source = new FakePlanArtifactSource();
            source.Enqueue(new PlanArtifactsRead(SessionPlansReadKind.Ready, new PlanArtifactsResponseDto {
                Artifacts = [new PlanArtifactDto {
                    ArtifactId = "a", Kind = "plan", Title = "p", Source = "declared", SessionId = "0123456789abcdef0123456789abcdef", Path = "docs/p.md",
                    Content = "# p", ContentState = "ok", IsComplete = true, IsConfirmed = true, ContentHash = "h", Version = 1,
                    DiscoveredAt = DateTimeOffset.UnixEpoch, Confidence = "high", Reason = "declared", IsPrimary = true,
                }],
            }));
            var vm = BuildRemote(sessionId: "0123456789abcdef0123456789abcdef", planArtifacts: source);
            await (vm.Artefacts.PendingReadForTesting ?? Task.CompletedTask);

            await Assert.That(vm.ShowsArtefactsTab).IsTrue();
            await vm.ShowArtefactsCommand.Execute();
            await Assert.That(vm.IsArtefactsActive).IsTrue();
            await Assert.That(vm.ShowsChatPane).IsFalse();
            await Assert.That(vm.ShowsArtefactsPane).IsTrue();
            await vm.TeardownAsync();
        });
    }
```

Name the suite's builder as it is actually named there; the `sessionId` must reach the `AgentRow` the builder constructs.

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet build test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj 2>&1 | grep -E 'error' | head`
Expected: errors on `WorkspaceTab.Artefacts`, `Artefacts`, `ShowsArtefactsTab`, `OpenCard`, `planArtifacts`.

- [ ] **Step 4: The work-context pane owns the switch for the local lane**

In `src/Capacitor.App/ViewModels/WorkContextViewModel.cs`:

Constructor signature (line 243) gains a trailing `ArtefactsTabViewModel? artefacts = null`; in the body, after `Plan = …`:

```csharp
        Artefacts = artefacts;
        OpenArtefactsCommand = ReactiveCommand.Create(() => Artefacts?.RequestOpen());
        _disposables.Add(OpenArtefactsCommand);
```

Members, beside `Plan`:

```csharp
    /// The Artefacts tab's model, when this pane's workspace has one; the pane shows its summary
    /// row and switches its session beside the plan's.
    public ArtefactsTabViewModel? Artefacts { get; }
    public ReactiveCommand<Unit, Unit> OpenArtefactsCommand { get; }
```

In `RefreshCommand`'s body, after `Plan.Refresh();`: `Artefacts?.Refresh();`. In `SwitchSession(string id)`, after `Plan.SwitchSession(id);`:

```csharp
        Artefacts?.SwitchSession(id, WorktreePath ?? RepositoryPath);
```

In `OnTick`, after `Plan.Refresh();`:

```csharp
        if (Artefacts is { IsShown: true } artefacts) artefacts.Refresh();
```

In `OnSignInCompleted`, after `Plan.Refresh();`: `Artefacts?.Refresh();`. In `TeardownAsync`, after `await Plan.TeardownAsync();`: the pane does not own the tab's lifetime, so nothing; the workspace tears it down.

- [ ] **Step 5: The workspace**

In `src/Capacitor.App/ViewModels/WorkspaceViewModel.cs`:

Line 13: `public enum WorkspaceTab { Chat, Terminal, PullRequest, Artefacts }`.

Constructor signature gains `IPlanArtifactSource? planArtifacts = null` after `plans`. Before `WorkContext = new WorkContextViewModel(...)`:

```csharp
        Artefacts = new ArtefactsTabViewModel(planArtifacts, planActivity, time, opener: opener);
        Artefacts.OpenRequested += ShowArtefacts;
        _disposables.Add(Disposable.Create(() => Artefacts.OpenRequested -= ShowArtefacts));
        Artefacts.WhenAnyValue(a => a.HasAny).Subscribe(has => {
            ShowsArtefactsTab = has;
            if (!has && IsArtefactsActive) ActiveTab = WorkspaceTab.Chat;
        }).DisposeWith(_disposables);
```

and pass `artefacts: Artefacts` to the `WorkContextViewModel` constructor. (`opener:` on `ArtefactsTabViewModel` is the parameter Task 10 adds; add it there now as `IUrlOpener? opener = null` so the two tasks meet.) Where `Chat = new ChatTabViewModel(...)` is built, pass `openCard: OpenCard`.

Members:

```csharp
    public ArtefactsTabViewModel Artefacts { get; }
    bool _showsArtefactsTab;
    public bool ShowsArtefactsTab {
        get => _showsArtefactsTab;
        private set {
            this.RaiseAndSetIfChanged(ref _showsArtefactsTab, value);
            this.RaisePropertyChanged(nameof(ShowsSurfaceSwitch));
        }
    }
    public bool ShowsSurfaceSwitch => ShowsTerminalTab || ShowsPullRequestTab || ShowsArtefactsTab;
    public bool IsArtefactsActive => ActiveTab == WorkspaceTab.Artefacts;
    public ReactiveCommand<Unit, Unit> ShowArtefactsCommand { get; }

    void ShowArtefacts() { if (ShowsArtefactsTab) ActiveTab = WorkspaceTab.Artefacts; }

    /// A chat card's Open: a declared document selects in the tab, a page goes to the browser.
    internal void OpenCard(ToolCard card) {
        if (card.DocumentPath is { } path && Artefacts.OpenDocument(path)) return;
        if (card.Url is { } url) LinkPolicy.Open(_opener, url);
    }
```

In the `ActiveTab` setter, after the existing projections are raised, add `this.RaisePropertyChanged(nameof(IsArtefactsActive)); Artefacts.IsShown = IsArtefactsActive;`. Beside the other tab commands: `ShowArtefactsCommand = ReactiveCommand.Create(ShowArtefacts); _disposables.Add(ShowArtefactsCommand);`. Keep the `IUrlOpener` in a field `_opener` if the constructor does not already (check; `WorkContext` and `PullRequests` receive `opener`, so the parameter exists). In `TeardownAsync`, beside `WorkContext`'s teardown: `await Artefacts.TeardownAsync();`.

- [ ] **Step 6: The remote lane**

In `src/Capacitor.App/ViewModels/RemoteSessionViewModel.cs`:

Line 14: `public enum RemoteTab { Chat, Terminal, Artefacts }`. Constructor signature gains `IPlanArtifactSource? planArtifacts = null` after `surfaceFactory`. Before `Chat = new ChatTabViewModel(...)`:

```csharp
        var planActivity = new PlanActivity();
        Artefacts = new ArtefactsTabViewModel(planArtifacts, planActivity, time, opener: opener);
        Artefacts.OpenRequested += ShowArtefacts;
        Artefacts.WhenAnyValue(a => a.HasAny).Subscribe(has => {
            ShowsArtefactsTab = has;
            if (!has && IsArtefactsActive) ActiveTab = RemoteTab.Chat;
        }).DisposeWith(_disposables);
        _sessionIds.Where(sid => sid is { Length: > 0 }).DistinctUntilChanged()
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(sid => Artefacts.SwitchSession(sid!, null)).DisposeWith(_disposables);
```

Pass `planActivity: planActivity` and `openCard: OpenCard` to the `ChatTabViewModel` constructor. Members mirror the workspace's (`Artefacts`, `ShowsArtefactsTab`, `IsArtefactsActive`, `ShowArtefactsCommand`, `ShowArtefacts()`, `OpenCard`), plus:

```csharp
    public bool ShowsSurfaceSwitch => ShowsTerminalTab || ShowsArtefactsTab;
    public bool ShowsArtefactsPane => ShowsPanes && IsArtefactsActive;
```

`ShowsChatPane` must become false while `IsArtefactsActive` (it is `ShowsPanes && IsChatActive` today or equivalent; keep that shape). `RaiseTabProjections` raises `IsArtefactsActive` and `ShowsArtefactsPane` too, and the `ActiveTab` setter sets `Artefacts.IsShown = IsArtefactsActive`. `TeardownAsync` awaits `Artefacts.TeardownAsync()` and unhooks `OpenRequested`.

- [ ] **Step 7: Thread the source in the app**

In `src/Capacitor.App/App.axaml.cs`, `BuildWorkspace` passes `planArtifacts: planArtifacts` after `plans: plans`, and `BuildRemote` passes `planArtifacts: planArtifacts` as its last argument.

- [ ] **Step 8: Run the tests to verify they pass**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/WorkspaceViewModelTests/*"` and `--treenode-filter "/*/*/RemoteSessionViewModelTests/*"`, then `--treenode-filter "/*/*/WorkContextViewModelTests/*"`.
Expected: green. The pane tests construct `WorkContextViewModel` without an `artefacts` argument and keep passing.

- [ ] **Step 9: Commit**

```bash
git add src/Capacitor.App/ViewModels/WorkspaceViewModel.cs src/Capacitor.App/ViewModels/WorkContextViewModel.cs src/Capacitor.App/ViewModels/RemoteSessionViewModel.cs src/Capacitor.App/App.axaml.cs test/Capacitor.App.Tests.Unit/WorkspaceViewModelTests.cs test/Capacitor.App.Tests.Unit/RemoteSessionViewModelTests.cs
git commit -m "Add the Artefacts tab to the local and remote workspaces (#1098)" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

### Task 10: The views: master list, document reader, segment position, header icon

**Files:**
- Create: `src/Capacitor.App/Views/DocumentReader.axaml`, `src/Capacitor.App/Views/DocumentReader.axaml.cs`
- Create: `src/Capacitor.App/Views/ArtefactsView.axaml`, `src/Capacitor.App/Views/ArtefactsView.axaml.cs`
- Modify: `src/Capacitor.App/ViewModels/ArtefactsTabViewModel.cs` (`IUrlOpener? opener`, `OpenLinkCommand`)
- Modify: `src/Capacitor.App/Views/WorkspaceView.axaml:36-50,138`, `src/Capacitor.App/Views/WorkspaceView.axaml.cs:33-46`
- Modify: `src/Capacitor.App/Views/RemoteSessionView.axaml:30-43,55-60`, `src/Capacitor.App/Views/RemoteSessionView.axaml.cs`
- Test: `test/Capacitor.App.Tests.Unit/WorkspaceViewSmokeTests.cs`, `test/Capacitor.App.Tests.Unit/RemoteSessionViewSmokeTests.cs`

**Interfaces:**
- Consumes: `ArtefactsTabViewModel`, `DocumentRow`, `DocumentReaderViewModel`, `MarkdownView`.
- Produces: `ArtefactsView : UserControl` (DataContext `ArtefactsTabViewModel`), `DocumentReader : UserControl` (DataContext `DocumentReaderViewModel`); named controls `ArtefactsTabButton`, `ArtefactsHost` (workspace), `ArtefactsPane` (remote), `DocumentList`, `DocumentReaderHost`, `DocumentScroll`, `DocumentNotice`, `OpenInWebButton` as an icon button on both views; `ArtefactsTabViewModel.OpenLinkCommand`.

- [ ] **Step 1: Write the failing smoke tests**

In `test/Capacitor.App.Tests.Unit/WorkspaceViewSmokeTests.cs`, give `Build` an `IPlanArtifactSource? planArtifacts = null` parameter passed as `planArtifacts:`, add the `Doc`/`Ready` helpers from Task 9's workspace test (or move them into `WorkspaceFixtures` and `using static` them from both suites), and add:

```csharp
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_artefacts_position_shows_with_a_document_and_hosts_the_list_and_reader() {
        await RunOnUiAsync(async () => {
            var source = new FakePlanArtifactSource();
            source.Enqueue(Ready(Doc("docs/x-design.md")));
            var (view, vm, daemon, _) = Build(planArtifacts: source);
            var window = new Window { Content = view, Width = 1000, Height = 640 };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            await Assert.That(Find<Button>(window, "ArtefactsTabButton")!.IsEffectivelyVisible).IsFalse();

            daemon.Agents.AddOrUpdate(WorkspaceFixtures.Agent(AgentId, "claude", hasTerminal: true, repoPath: "/repo/myproj", sessionId: AgentId));
            await (vm.Terminal.PendingResolveWorkForTesting ?? Task.CompletedTask);
            await (vm.Artefacts.PendingReadForTesting ?? Task.CompletedTask);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            await Assert.That(Find<Button>(window, "ArtefactsTabButton")!.IsEffectivelyVisible).IsTrue();
            await Assert.That(Find<ContentControl>(window, "ArtefactsHost")!.Content).IsNull();

            await vm.ShowArtefactsCommand.Execute();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            await Assert.That(Find<ContentControl>(window, "ArtefactsHost")!.Content).IsTypeOf<ArtefactsView>();
            await Assert.That(Find<ItemsControl>(window, "DocumentList")!.IsEffectivelyVisible).IsTrue();
            await Assert.That(Find<ContentControl>(window, "DocumentReaderHost")!.IsEffectivelyVisible).IsTrue();

            await vm.Artefacts.SelectCommand.Execute(vm.Artefacts.Documents[0]);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            await Assert.That(Find<ScrollViewer>(window, "DocumentScroll")!.IsEffectivelyVisible).IsTrue();
            await Assert.That(Find<Border>(window, "DocumentNotice")!.IsEffectivelyVisible).IsFalse();

            window.Close();
            await vm.TeardownAsync();
        });
    }

    /// The header's right side is icons: the segment is what grows.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Open_in_web_is_an_icon_button_with_a_tooltip() {
        await RunOnUiAsync(async () => {
            var (window, vm, _, _) = await ShowPtyAsync();
            var button = Find<Button>(window, "OpenInWebButton")!;
            await Assert.That(button.Content).IsNotTypeOf<string>();
            await Assert.That(ToolTip.GetTip(button)).IsEqualTo("Open in web");
            await Assert.That(button.Classes.Contains("kcapIcon")).IsTrue();
            window.Close();
            await vm.TeardownAsync();
        });
    }
```

In `test/Capacitor.App.Tests.Unit/RemoteSessionViewSmokeTests.cs`, with the suite's own builder taking `planArtifacts:` and a document enqueued as in Task 9's remote test, add one test that `ArtefactsTabButton` becomes visible after the read, that executing `ShowArtefactsCommand` makes `ArtefactsPane`'s content an `ArtefactsView`, and that the remote `OpenInWebButton` carries the "Open in web" tooltip.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet build test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj 2>&1 | grep -E 'error' | head`
Expected: errors naming `ArtefactsView` and `planArtifacts`.

- [ ] **Step 3: Give the tab a link command**

In `ArtefactsTabViewModel`, whose constructor already takes the trailing `IUrlOpener? opener = null` from Task 9, add:

```csharp
    public ReactiveCommand<string, Unit> OpenLinkCommand { get; }
```

built in the constructor as `OpenLinkCommand = ReactiveCommand.Create<string>(url => { if (opener is not null) LinkPolicy.Open(opener, url); });` and disposed in `TeardownAsync`. Add `using Capacitor.App.Services;` (already present) and `using System.Reactive;`.

- [ ] **Step 4: Create `DocumentReader`**

`src/Capacitor.App/Views/DocumentReader.axaml`:

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="clr-namespace:Capacitor.App.ViewModels"
             xmlns:views="clr-namespace:Capacitor.App.Views"
             x:Class="Capacitor.App.Views.DocumentReader"
             x:DataType="vm:DocumentReaderViewModel"
             Background="{StaticResource KcapCanvasBrush}">
    <Grid RowDefinitions="Auto,*">
        <StackPanel Margin="24,20,24,12" Spacing="8">
            <StackPanel Orientation="Horizontal" Spacing="10">
                <Border Background="{StaticResource KcapSurfaceRaisedBrush}" BorderBrush="{StaticResource KcapBorderBrush}" BorderThickness="1"
                        CornerRadius="4" Padding="6,1">
                    <TextBlock x:Name="DocumentKind" Text="{Binding KindLabel}" FontSize="10.5" FontWeight="SemiBold"
                               Foreground="{StaticResource KcapMutedBrush}" />
                </Border>
                <TextBlock Text="{Binding Path}" FontSize="12.5" Foreground="{StaticResource KcapFaintBrush}"
                           TextTrimming="CharacterEllipsis" VerticalAlignment="Center" ToolTip.Tip="{Binding Path}" />
            </StackPanel>
            <TextBlock x:Name="DocumentTitle" Text="{Binding FileName}" FontSize="22" FontWeight="SemiBold" TextWrapping="Wrap"
                       Foreground="{StaticResource KcapTextBrush}" />
            <TextBlock FontSize="12.5" Foreground="{StaticResource KcapFaintBrush}">
                <Run Text="{Binding DeclaredLabel}" /><Run Text=" · " /><Run Text="{Binding SizeLabel}" />
            </TextBlock>
            <Border x:Name="DocumentNotice" IsVisible="{Binding HasNotice}" HorizontalAlignment="Left"
                    Background="{StaticResource KcapWarningDimBrush}" CornerRadius="6" Padding="10,6">
                <TextBlock Text="{Binding Notice}" FontSize="12.5" Foreground="{StaticResource KcapWarningBrush}" TextWrapping="Wrap" />
            </Border>
        </StackPanel>
        <!-- MarkdownView disables its own scrollbars; this viewer is the one scroller. -->
        <ScrollViewer x:Name="DocumentScroll" Grid.Row="1" VerticalScrollBarVisibility="Auto" HorizontalScrollBarVisibility="Disabled"
                      IsVisible="{Binding HasBody}">
            <views:MarkdownView Margin="24,0,24,24" Text="{Binding Body}" Flavor="GitHub"
                                OpenLink="{Binding $parent[views:ArtefactsView].((vm:ArtefactsTabViewModel)DataContext).OpenLinkCommand}" />
        </ScrollViewer>
    </Grid>
</UserControl>
```

`src/Capacitor.App/Views/DocumentReader.axaml.cs`:

```csharp
using Avalonia.Controls;

namespace Capacitor.App.Views;

public partial class DocumentReader : UserControl {
    public DocumentReader() => InitializeComponent();
}
```

- [ ] **Step 5: Create `ArtefactsView`**

`src/Capacitor.App/Views/ArtefactsView.axaml`:

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="clr-namespace:Capacitor.App.ViewModels"
             xmlns:views="clr-namespace:Capacitor.App.Views"
             x:Class="Capacitor.App.Views.ArtefactsView"
             x:DataType="vm:ArtefactsTabViewModel"
             Background="{StaticResource KcapCanvasBrush}">
    <UserControl.Styles>
        <Style Selector="Button.artefactRow">
            <Setter Property="Background" Value="Transparent" />
            <Setter Property="BorderThickness" Value="0" />
            <Setter Property="Padding" Value="10,7" />
            <Setter Property="CornerRadius" Value="7" />
            <Setter Property="HorizontalAlignment" Value="Stretch" />
            <Setter Property="HorizontalContentAlignment" Value="Stretch" />
            <Setter Property="Cursor" Value="Hand" />
        </Style>
        <Style Selector="Button.artefactRow /template/ ContentPresenter#PART_ContentPresenter">
            <Setter Property="Background" Value="Transparent" />
        </Style>
        <Style Selector="Button.artefactRow:pointerover /template/ ContentPresenter#PART_ContentPresenter">
            <Setter Property="Background" Value="{StaticResource KcapSurfaceRaisedBrush}" />
        </Style>
        <!-- Selection is info blue, as the rail row and the segment are. -->
        <Style Selector="Button.artefactRow.selected /template/ ContentPresenter#PART_ContentPresenter">
            <Setter Property="Background" Value="{StaticResource KcapSelectionBrush}" />
        </Style>
    </UserControl.Styles>
    <Grid ColumnDefinitions="240,*">
        <Border Grid.Column="0" Background="{StaticResource KcapSurfaceBrush}" BorderBrush="{StaticResource KcapBorderBrush}" BorderThickness="0,0,1,0">
            <ScrollViewer VerticalScrollBarVisibility="Auto" HorizontalScrollBarVisibility="Disabled">
                <StackPanel Margin="10,14,10,14" Spacing="4">
                    <TextBlock Text="DOCUMENTS" FontSize="11.5" FontWeight="Bold" LetterSpacing="1.2"
                               Foreground="{StaticResource KcapMutedBrush}" Margin="10,0,0,6" />
                    <ItemsControl x:Name="DocumentList" ItemsSource="{Binding Documents}">
                        <ItemsControl.ItemsPanel>
                            <ItemsPanelTemplate>
                                <StackPanel Spacing="2" />
                            </ItemsPanelTemplate>
                        </ItemsControl.ItemsPanel>
                        <ItemsControl.ItemTemplate>
                            <DataTemplate x:DataType="vm:DocumentRow">
                                <Button Classes="artefactRow" Classes.selected="{Binding IsSelected}"
                                        Command="{Binding $parent[ItemsControl].((vm:ArtefactsTabViewModel)DataContext).SelectCommand}"
                                        CommandParameter="{Binding}" ToolTip.Tip="{Binding Path}">
                                    <StackPanel Spacing="2">
                                        <TextBlock Text="{Binding FileName}" FontSize="13" Foreground="{StaticResource KcapTextBrush}" TextTrimming="CharacterEllipsis" />
                                        <StackPanel Orientation="Horizontal" Spacing="6">
                                            <TextBlock Text="{Binding Kind}" FontSize="11" Foreground="{StaticResource KcapFaintBrush}" />
                                            <Border IsVisible="{Binding HasStateChip}" Background="{StaticResource KcapWarningDimBrush}" CornerRadius="4" Padding="5,0">
                                                <TextBlock Text="{Binding StateChip}" FontSize="10.5" Foreground="{StaticResource KcapWarningBrush}" />
                                            </Border>
                                        </StackPanel>
                                    </StackPanel>
                                </Button>
                            </DataTemplate>
                        </ItemsControl.ItemTemplate>
                    </ItemsControl>
                </StackPanel>
            </ScrollViewer>
        </Border>
        <Panel Grid.Column="1">
            <ContentControl x:Name="DocumentReaderHost" Content="{Binding Reader}">
                <ContentControl.ContentTemplate>
                    <DataTemplate x:DataType="vm:DocumentReaderViewModel">
                        <views:DocumentReader />
                    </DataTemplate>
                </ContentControl.ContentTemplate>
            </ContentControl>
            <TextBlock Text="Select a document to read it." FontSize="13.5" Foreground="{StaticResource KcapMutedBrush}"
                       HorizontalAlignment="Center" VerticalAlignment="Center"
                       IsVisible="{Binding Reader, Converter={x:Static ObjectConverters.IsNull}}" />
        </Panel>
    </Grid>
</UserControl>
```

`src/Capacitor.App/Views/ArtefactsView.axaml.cs`:

```csharp
using Avalonia.Controls;

namespace Capacitor.App.Views;

public partial class ArtefactsView : UserControl {
    public ArtefactsView() => InitializeComponent();
}
```

- [ ] **Step 6: Host it in the workspace and compress the header**

In `src/Capacitor.App/Views/WorkspaceView.axaml`, after `PullRequestTabButton`:

```xml
                            <Button x:Name="ArtefactsTabButton" Content="Artefacts" Classes="kcapSegmentItem"
                                    Classes.active="{Binding IsArtefactsActive}" Command="{Binding ShowArtefactsCommand}"
                                    IsVisible="{Binding ShowsArtefactsTab}" />
```

Replace `OpenInWebButton`:

```xml
                    <Button x:Name="OpenInWebButton" Command="{Binding OpenInWebCommand}" IsVisible="{Binding HasAgent}"
                            Classes="kcapGhost kcapIcon" ToolTip.Tip="Open in web" AutomationProperties.Name="Open in web">
                        <Path Width="14" Height="14" Stretch="Uniform" Stroke="{StaticResource KcapMutedBrush}" StrokeThickness="1.6"
                              StrokeLineCap="Round" StrokeJoin="Round"
                              Data="M14,3 H21 V10 M21,3 L11,13 M18,13 V20 H4 V6 H11" />
                    </Button>
```

After `PullRequestHost`: `<ContentControl x:Name="ArtefactsHost" IsVisible="{Binding IsArtefactsActive}" />`.

In `src/Capacitor.App/Views/WorkspaceView.axaml.cs`, inside the `DataContextChanged` handler: reset `ArtefactsHost.Content = null;` beside `PullRequestHost.Content = null;`, and inside the posted callback, after the pull-request creation line:

```csharp
                    if (pair.Item1 == WorkspaceTab.Artefacts && ArtefactsHost.Content is null && model is not null)
                        ArtefactsHost.Content = new ArtefactsView { DataContext = model.Artefacts };
```

In `src/Capacitor.App/Views/RemoteSessionView.axaml`, add the same `ArtefactsTabButton` after `TerminalTabButton`, replace its "Open in web" button with the icon form above (`x:Name="OpenInWebButton"`, no `IsVisible`), and after the `TerminalPane` grid add `<ContentControl x:Name="ArtefactsPane" IsVisible="{Binding ShowsArtefactsPane}" />`. In `RemoteSessionView.axaml.cs`, create the `ArtefactsView` lazily the first time `ActiveTab == RemoteTab.Artefacts`, following whatever the file already does on `DataContextChanged`; if it does nothing there yet, subscribe to `WhenAnyValue(vm => vm.ActiveTab)` as `WorkspaceView` does.

- [ ] **Step 7: Run the smoke tests and the XAML build**

Run: `dotnet build src/Capacitor.App/Capacitor.App.csproj 2>&1 | grep -E 'AVLN|error|warning' | head` — must print nothing. Then `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/WorkspaceViewSmokeTests/*"` and the remote smoke suite.
Expected: green. A `$parent[views:ArtefactsView]` binding that cannot find its ancestor is reported as an AVLN or a binding error in the test output; if so, the reader is not inside the `ArtefactsView` tree at bind time: give `DocumentReader` an `OpenLink` styled property set from `ArtefactsView.axaml` instead.

- [ ] **Step 8: Commit**

```bash
git add src/Capacitor.App/Views/DocumentReader.axaml src/Capacitor.App/Views/DocumentReader.axaml.cs src/Capacitor.App/Views/ArtefactsView.axaml src/Capacitor.App/Views/ArtefactsView.axaml.cs src/Capacitor.App/ViewModels/ArtefactsTabViewModel.cs src/Capacitor.App/Views/WorkspaceView.axaml src/Capacitor.App/Views/WorkspaceView.axaml.cs src/Capacitor.App/Views/RemoteSessionView.axaml src/Capacitor.App/Views/RemoteSessionView.axaml.cs test/Capacitor.App.Tests.Unit/WorkspaceViewSmokeTests.cs test/Capacitor.App.Tests.Unit/RemoteSessionViewSmokeTests.cs
git commit -m "Show the Artefacts list and document reader in the centre (#1098)" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

### Task 11: Pane rows open the tab, the summary row, and the README

**Files:**
- Create: `src/Capacitor.App/ViewModels/DocumentPaths.cs`
- Modify: `src/Capacitor.App/ViewModels/PlanDocumentRow.cs`
- Modify: `src/Capacitor.App/ViewModels/PlanSectionViewModel.cs`
- Modify: `src/Capacitor.App/ViewModels/DocumentRow.cs` (`MatchesPath` delegates)
- Modify: `src/Capacitor.App/ViewModels/WorkspaceViewModel.cs` (plan ↔ tab wiring)
- Modify: `src/Capacitor.App/Views/WorkContextView.axaml:651-713`
- Modify: `README.md:98`
- Test: `test/Capacitor.App.Tests.Unit/PlanSectionViewModelTests.cs`, `test/Capacitor.App.Tests.Unit/WorkContextViewSmokeTests.cs`, `test/Capacitor.App.Tests.Unit/WorkspaceViewModelTests.cs`

**Interfaces:**
- Consumes: `ArtefactsTabViewModel.OpenDocument`, `.Selected`, `.HasAny`, `.SummaryText`; `WorkContextViewModel.Artefacts`, `.OpenArtefactsCommand`.
- Produces: `static bool DocumentPaths.Match(string a, string b)`; `PlanDocumentRow` as a class with `Kind`, `Path`, `FileName`, `IsOpen`; `PlanSectionViewModel.OpenDocument` (`Action<string>?`), `OpenDocumentCommand` (`ReactiveCommand<PlanDocumentRow, Unit>`), `void MarkOpen(string? path)`.

- [ ] **Step 1: Write the failing view-model tests**

Append to `test/Capacitor.App.Tests.Unit/PlanSectionViewModelTests.cs` inside the class:

```csharp
    /// A document row is a way into the tab: the command hands the declared path to whoever
    /// opens documents, and the open one stays marked through a refresh.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_document_row_opens_by_path_and_the_open_mark_survives_a_refresh() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            var opened = new List<string>();
            h.Vm.OpenDocument = opened.Add;
            h.Source.Enqueue(Ready(Plan("p1", documents: [Doc("design", "docs/x-design.md"), Doc("plan", "docs/x.md")])));
            await h.SwitchAsync(SessionA);

            await h.Vm.OpenDocumentCommand.Execute(h.Vm.Documents[1]);
            await Assert.That(opened).IsEquivalentTo(new[] { "docs/x-design.md" });

            h.Vm.MarkOpen("/Users/me/repo/docs/x-design.md");
            await Assert.That(h.Vm.Documents[1].IsOpen).IsTrue();
            await Assert.That(h.Vm.Documents[0].IsOpen).IsFalse();

            h.Source.Enqueue(Ready(Plan("p1", documents: [Doc("design", "docs/x-design.md"), Doc("plan", "docs/x.md"), Doc("spec", "docs/s.md")])));
            await h.RefreshAsync();
            await Assert.That(h.Vm.Documents.Single(d => d.Kind == "design").IsOpen).IsTrue();

            h.Vm.MarkOpen(null);
            await Assert.That(h.Vm.Documents.Any(d => d.IsOpen)).IsFalse();
            await h.Vm.TeardownAsync();
        });
    }
```

(Documents sort plan, spec, design, so index 1 is the design doc in the first read.)

Append to `test/Capacitor.App.Tests.Unit/WorkspaceViewModelTests.cs`:

```csharp
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_pane_document_row_opens_the_tab_and_the_tabs_selection_marks_the_row() {
        await RunOnUiAsync(async () => {
            var daemon = new FakeDaemonClientService();
            var source = new FakePlanArtifactSource();
            source.Enqueue(Ready(Doc("docs/x-design.md")));
            var plans = new FakePlanSource();
            plans.Enqueue(new SessionPlansRead(SessionPlansReadKind.Ready, [new SessionPlanDto {
                PlanId = "p1", IsCurrent = true, Tasks = [],
                Documents = [new PlanDocumentDto { DocumentKey = "k", Kind = "design", Path = "docs/x-design.md" }],
            }]));
            var vm = Build(daemon, NewActions(new ScriptedLocalControlOps(), new RecordingNotifier(), new RecordingOpener()), new FakeTerminalAttachClientFactory(), new FakeTimeProvider(), planArtifacts: source, plans: plans);
            daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: true, repoPath: "/repo/x", sessionId: Session));
            await (vm.Terminal.PendingResolveWorkForTesting ?? Task.CompletedTask);
            await (vm.Artefacts.PendingReadForTesting ?? Task.CompletedTask);
            await (vm.WorkContext.Plan.PendingReadForTesting ?? Task.CompletedTask);

            var row = vm.WorkContext.Plan.Documents.Single();
            await vm.WorkContext.Plan.OpenDocumentCommand.Execute(row);
            await Assert.That(vm.ActiveTab).IsEqualTo(WorkspaceTab.Artefacts);
            await Assert.That(vm.Artefacts.Selected!.Path).IsEqualTo("docs/x-design.md");
            await Assert.That(row.IsOpen).IsTrue();
            await vm.TeardownAsync();
        });
    }
```

`Build` gains `IPlanSource? plans = null` passed as `plans: plans`.

- [ ] **Step 2: Write the failing smoke test**

In `test/Capacitor.App.Tests.Unit/WorkContextViewSmokeTests.cs`, give `Host` an `ArtefactsTabViewModel` built over a `FakePlanArtifactSource` (`public FakePlanArtifactSource Artefacts { get; } = new();` and `new ArtefactsTabViewModel(Artefacts, PlanActivity, Time)` passed to the pane as `artefacts:`), switch it in `ShowAsync` with `Vm.Artefacts!.SwitchSession(SessionA, null)` after the presence push, await its `PendingReadForTesting`, and add:

```csharp
    /// The pane's two ways into the tab: a clickable document row and the summary row.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Document_rows_are_buttons_and_the_summary_row_shows_once_the_tab_has_something() {
        await RunOnUiAsync(async () => {
            await using var host = new Host();
            host.Plans.Enqueue(PlanRead([new PlanDocumentDto { DocumentKey = "k1", Kind = "design", Path = "docs/x-design.md" }]));
            host.Artefacts.Enqueue(new PlanArtifactsRead(SessionPlansReadKind.Ready, new PlanArtifactsResponseDto {
                Artifacts = [new PlanArtifactDto {
                    ArtifactId = "a", Kind = "design", Title = "x", Source = "declared", SessionId = SessionA, Path = "docs/x-design.md",
                    Content = "# x", ContentState = "ok", IsComplete = true, IsConfirmed = true, ContentHash = "h", Version = 1,
                    DiscoveredAt = DateTimeOffset.UnixEpoch, Confidence = "high", Reason = "declared", IsPrimary = true,
                }],
            }));
            var opens = 0;
            host.Vm.Artefacts!.OpenRequested += () => opens++;
            await host.ShowAsync(KeyOnlyRead());

            await Assert.That(host.Find<Button>("ArtefactsSummaryRow").IsEffectivelyVisible).IsTrue();
            await Assert.That(host.Find<TextBlock>("ArtefactsSummaryText").Text).IsEqualTo("1 document");
            await host.Vm.OpenArtefactsCommand.Execute();
            await Assert.That(opens).IsEqualTo(1);

            await host.Vm.Plan.ToggleCommand.Execute();
            Dispatcher.UIThread.RunJobs();
            host.Window.UpdateLayout();
            var rowButton = host.Window.GetVisualDescendants().OfType<Button>().Single(b => b.DataContext is PlanDocumentRow);
            await Assert.That(rowButton.Classes.Contains("copyValue")).IsTrue();
        });
    }
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet build test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj 2>&1 | grep -E 'error' | head`
Expected: errors on `OpenDocument`, `OpenDocumentCommand`, `MarkOpen`, `IsOpen`.

- [ ] **Step 4: Share the path match and make the row a class**

`src/Capacitor.App/ViewModels/DocumentPaths.cs`:

```csharp
namespace Capacitor.App.ViewModels;

/// Paths from two machines: a declared repo-relative path and a working-copy or card path. They
/// match when one ends with the other on a segment boundary, whichever separator either uses.
public static class DocumentPaths {
    public static bool Match(string a, string b) {
        var left = Normalise(a);
        var right = Normalise(b);
        if (left.Length == 0 || right.Length == 0) return false;
        if (left == right) return true;
        return left.EndsWith("/" + right, StringComparison.Ordinal) || right.EndsWith("/" + left, StringComparison.Ordinal);
    }

    static string Normalise(string path) => path.Replace('\\', '/').TrimEnd('/');
}
```

In `DocumentRow`, replace the body of `MatchesPath` with `=> DocumentPaths.Match(Path, other);` and delete its private `Normalise`.

`src/Capacitor.App/ViewModels/PlanDocumentRow.cs`:

```csharp
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// A document the plan was declared from. `Path` is the declaring machine's, relative to its
/// repository root, so the file name is cut on either separator rather than this platform's.
public sealed class PlanDocumentRow(string kind, string path) : ReactiveObject {
    public string Kind { get; } = kind;
    public string Path { get; } = path;
    public string FileName { get; } = path[(path.LastIndexOfAny(['/', '\\']) + 1)..];

    bool _isOpen;
    /// True while the Artefacts tab shows this document; the row wears the location mark.
    public bool IsOpen { get => _isOpen; internal set => this.RaiseAndSetIfChanged(ref _isOpen, value); }

    public bool Same(PlanDocumentRow other) => Kind == other.Kind && Path == other.Path;
}
```

- [ ] **Step 5: The section opens and marks**

In `PlanSectionViewModel`:

```csharp
    /// Whoever opens documents; the workspace points it at the Artefacts tab.
    public Action<string>? OpenDocument { get; set; }
    public ReactiveCommand<PlanDocumentRow, Unit> OpenDocumentCommand { get; }
    string? _openPath;
```

In the constructor: `OpenDocumentCommand = ReactiveCommand.Create<PlanDocumentRow>(row => OpenDocument?.Invoke(row.Path));` and dispose it in `TeardownAsync`. Add:

```csharp
    /// Marks the row whose path matches; null clears the mark.
    public void MarkOpen(string? path) {
        _openPath = path;
        foreach (var row in _documents) row.IsOpen = path is not null && DocumentPaths.Match(row.Path, path);
    }
```

In `Show`, replace the document block:

```csharp
        var documents = plan.Documents
            .OrderBy(document => KindRank(document.Kind))
            .Select(document => new PlanDocumentRow(document.Kind, document.Path))
            .ToList();
        if (documents.Count != _documents.Count || documents.Where((row, i) => !row.Same(_documents[i])).Any()) {
            _documents.Clear();
            _documents.AddRange(documents);
            MarkOpen(_openPath);
        }
```

- [ ] **Step 6: Wire the plan to the tab in the workspace**

In `WorkspaceViewModel`, after `WorkContext = new WorkContextViewModel(...)`:

```csharp
        WorkContext.Plan.OpenDocument = path => Artefacts.OpenDocument(path);
        Artefacts.WhenAnyValue(a => a.Selected).Subscribe(selected => WorkContext.Plan.MarkOpen(selected?.Path)).DisposeWith(_disposables);
```

- [ ] **Step 7: The pane markup**

In `src/Capacitor.App/Views/WorkContextView.axaml`, in the pane's `Styles` block add after the `Button.copyValue` styles:

```xml
        <Style Selector="Button.copyValue.open /template/ ContentPresenter#PART_ContentPresenter">
            <Setter Property="Background" Value="{StaticResource KcapPurpleDimBrush}" />
        </Style>
        <Style Selector="Button.copyValue.open TextBlock.docName">
            <Setter Property="Foreground" Value="{StaticResource KcapPurpleBrush}" />
        </Style>
```

Before `<StackPanel x:Name="PlanSection" …>` add the summary row:

```xml
                <!-- The Artefacts tab's summary; the tab itself lives in the centre. -->
                <Button x:Name="ArtefactsSummaryRow" Classes="copyValue" Margin="0,18,0,0" HorizontalAlignment="Stretch"
                        Command="{Binding OpenArtefactsCommand}"
                        IsVisible="{Binding Artefacts.HasAny, FallbackValue=False}">
                    <Grid ColumnDefinitions="*,Auto" ColumnSpacing="10">
                        <TextBlock Text="ARTEFACTS" Classes="eyebrow" />
                        <StackPanel Grid.Column="1" Orientation="Horizontal" Spacing="8" VerticalAlignment="Center">
                            <TextBlock x:Name="ArtefactsSummaryText" Text="{Binding Artefacts.SummaryText}" Classes="sectionMeta" />
                            <Path Classes="prChevron" />
                        </StackPanel>
                    </Grid>
                </Button>
```

If `Path.prChevron` is not reachable from this view, draw the chevron inline: `<Path Width="12" Height="12" Stretch="None" Stroke="{StaticResource KcapFaintBrush}" StrokeThickness="1.6" Data="M4.5,3 L7.5,6 L4.5,9" />`.

Replace the document row template inside `PlanDocumentList`:

```xml
                                    <DataTemplate x:DataType="vm:PlanDocumentRow">
                                        <Button Classes="copyValue" Classes.open="{Binding IsOpen}" HorizontalAlignment="Stretch"
                                                Command="{Binding $parent[ItemsControl].((vm:WorkContextViewModel)DataContext).Plan.OpenDocumentCommand}"
                                                CommandParameter="{Binding}" ToolTip.Tip="{Binding Path}">
                                            <Grid ColumnDefinitions="46,*,Auto" ColumnSpacing="6">
                                                <TextBlock Text="{Binding Kind}" FontSize="12" LineHeight="16" Foreground="{StaticResource KcapFaintBrush}" />
                                                <TextBlock Grid.Column="1" Classes="docName" Text="{Binding FileName}" FontSize="13" LineHeight="16"
                                                           Foreground="{StaticResource KcapTextBrush}" TextTrimming="CharacterEllipsis" />
                                                <Path Grid.Column="2" Width="8" Height="8" Stretch="None" VerticalAlignment="Center"
                                                      Stroke="{StaticResource KcapFaintBrush}" StrokeThickness="1.5" Data="M2,1 L5,4 L2,7" />
                                            </Grid>
                                        </Button>
                                    </DataTemplate>
```

`$parent[ItemsControl]` resolves to `PlanDocumentList`, whose DataContext is the pane's `WorkContextViewModel`; if the binding log reports a miss, name the pane root and use `$parent[views:WorkContextView]` instead.

- [ ] **Step 8: The README**

In `README.md` line 98, replace the sentence `Rows are not links.` at the end of the PLAN paragraph with:

```
Click a document to read it: it opens in the **Artefacts** tab beside Chat and Terminal, and the open one is marked in the list.
```

After that paragraph add:

```
The **Artefacts** tab appears in the centre switch when the session has declared a plan, spec or design document. It lists them on the left, kind and file name, with a chip when the server holds only a truncated body or none, and renders the chosen one on the right as the agent declared it; for a local session the reader says when the working copy has moved on from the declared text. The work-context pane shows a one-line summary ("ARTEFACTS · 2 documents") that opens the tab, and a remote session gets the same tab in its own switch.

Calls to the `kcap-*` MCP tools read in plain words in the chat ("Attached work item · AI-3084", "Saved memory · …", "Searched sessions · …") instead of the raw tool name, grouped and folded as other tool calls are. A call that produced something you can open becomes a card once its result lands: a published page carries Open and Copy link, a declared document carries Open into the Artefacts tab. Tools from other MCP servers read as "Server · tool name" with their first argument.
```

- [ ] **Step 9: Run everything**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj` (whole suite) and `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj`.
Expected: green. Then `dotnet build Capacitor.slnx 2>&1 | grep -E 'error|warning' | head` must print nothing (IDE0005 unused usings are build errors).

- [ ] **Step 10: Commit**

```bash
git add src/Capacitor.App/ViewModels/DocumentPaths.cs src/Capacitor.App/ViewModels/PlanDocumentRow.cs src/Capacitor.App/ViewModels/PlanSectionViewModel.cs src/Capacitor.App/ViewModels/DocumentRow.cs src/Capacitor.App/ViewModels/WorkspaceViewModel.cs src/Capacitor.App/Views/WorkContextView.axaml README.md test/Capacitor.App.Tests.Unit/PlanSectionViewModelTests.cs test/Capacitor.App.Tests.Unit/WorkContextViewSmokeTests.cs test/Capacitor.App.Tests.Unit/WorkspaceViewModelTests.cs
git commit -m "Open plan documents from the pane into the Artefacts tab (#1098)" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

## After the last task

- Run the full solution build and both test suites once more from a clean tree; then open the branch as a PR with the description following `.github/PULL_REQUEST_TEMPLATE.md`, referencing `Closes #1098` and `AI-3084`, and noting that slices 3 and 4 of the spec follow in their own plans.
- A live check in the running app is owed before merge: a Claude session that declares a design document and publishes a page, viewed locally and from the remote view.
- Names to verify against the tree before relying on them: `JsonElementExtensions.Obj`/`.Int` (Task 3), `RelativeTime`'s formatting method (Task 8), `Path.prChevron` and `Path.prExternal` reachability from the pane and the header (Tasks 10 and 11).
