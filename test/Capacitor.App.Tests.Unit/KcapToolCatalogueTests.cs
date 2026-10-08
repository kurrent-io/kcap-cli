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

    [Test]
    public async Task Pi_spells_a_kcap_tool_with_its_server_between_underscores() {
        var document = KcapToolCatalogue.Match("kcap_plans_declare_plan_document")!;
        await Assert.That(document.Tool).IsEqualTo("declare_plan_document");
        await Assert.That(document.Card).IsEqualTo(ToolCardKind.Document);

        var work = KcapToolCatalogue.Match("kcap_workitems_declare_work_item")!;
        await Assert.That(work.Category).IsEqualTo(ToolCategory.Work);
        await Assert.That(work.Tool).IsEqualTo("declare_work_item");
    }

    /// A suffix must be the whole bare name: `set_plan_tasks` is not `plan_tasks`, and a foreign
    /// server's `save_memory` is not ours when its server segment says otherwise.
    [Test]
    [Arguments("mcp__other__save_memory")]
    [Arguments("mcp__plugin_acme_acme__publish_artefact")]
    [Arguments("mcp__kcapfoo__save_memory")]
    [Arguments("kcap_unknown_save_memory")]
    [Arguments("kcap_")]
    [Arguments("kcap_plans")]
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
