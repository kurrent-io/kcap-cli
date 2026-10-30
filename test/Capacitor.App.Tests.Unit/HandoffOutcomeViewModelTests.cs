using Capacitor.App.ViewModels;
using Capacitor.Cli.Core.Handoff;

namespace Capacitor.App.Tests.Unit;

public class HandoffOutcomeViewModelTests {
    static HandoffOutcomeItemDto Item(string result, string label = "Plan A") => new("plan", label, result, null);

    [Test]
    public async Task A_refused_begin_names_its_source_with_no_items() {
        var vm = new HandoffOutcomeViewModel(HandoffOutcomeDto.Refused("s1", null, "server_unsupported"));

        await Assert.That(vm.Header).IsEqualTo("Continued from s1");
        await Assert.That(vm.Note).IsEqualTo("Couldn't carry over the previous session's work (server_unsupported).");
        await Assert.That(vm.Rows).IsEmpty();
    }

    [Test]
    public async Task A_source_active_refusal_reads_as_stayed_with_it() {
        var vm = new HandoffOutcomeViewModel(HandoffOutcomeDto.Refused("s1", "Old", "source_active"));

        await Assert.That(vm.Header).IsEqualTo("Continued from Old");
        await Assert.That(vm.Note).IsEqualTo("The previous session is active again, so its work items and plans stayed with it.");
    }

    [Test]
    public async Task Server_labels_are_folded_onto_one_line_and_capped() {
        var longTitle = new string('t', 200);
        var vm = new HandoffOutcomeViewModel(new("s1", "Fix\n  the\r\nwidgets", "done", null,
            [Item("carried", "Plan\nA"), Item("carried", longTitle)]));

        await Assert.That(vm.Header).IsEqualTo("Continued from Fix the widgets");
        await Assert.That(vm.Rows[0].Text).IsEqualTo("Plan A — carried over");
        await Assert.That(vm.Rows[1].Text).IsEqualTo($"{new string('t', 119)}… — carried over");
    }

    [Test]
    public async Task Interrupted_reads_as_cut_short() {
        var vm = new HandoffOutcomeViewModel(new("s1", "Old", "interrupted", null, []));

        await Assert.That(vm.Note).IsEqualTo("The hand-off was cut short before its work items and plans were carried over.");
        await Assert.That(vm.NoteNeedsAttention).IsTrue();
    }

    [Test]
    public async Task Running_says_it_is_carrying_over() {
        var vm = new HandoffOutcomeViewModel(HandoffOutcomeDto.Running("s1", "Old"));

        await Assert.That(vm.Note).IsEqualTo("Carrying over work items and plans…");
        await Assert.That(vm.NoteNeedsAttention).IsFalse();
    }

    [Test]
    public async Task Unknown_plans_read_as_couldnt_be_checked() {
        var vm = new HandoffOutcomeViewModel(new("s1", "Old", "done", null,
            [Item("carried", "Plan A"), Item("refused", "Plan B"), Item("skipped", "Plan C"), Item("failed", "Plan D"), Item("unknown", "Plan E")]));

        await Assert.That(vm.Rows.Select(r => r.Text)).IsEquivalentTo([
            "Plan A — carried over", "Plan B — refused", "Plan C — skipped", "Plan D — failed", "Plan E — couldn't be checked",
        ]);
        await Assert.That(vm.Rows.Select(r => r.NeedsAttention)).IsEquivalentTo([false, true, false, true, true]);
        await Assert.That(vm.Rows[0].IsCarried).IsTrue();
        await Assert.That(vm.Rows[2].IsNeutral).IsTrue();
        await Assert.That(vm.Note).IsNull();
    }

    static readonly HandoffOutcomeItemDto Carried = new("plan", "Plan A", "carried", null);

    [Test]
    public async Task A_rejected_sign_in_asks_for_a_new_one_not_a_code() {
        var none = new HandoffOutcomeViewModel(new("s1", "Old", "unauthorized", null, []));
        var some = new HandoffOutcomeViewModel(new("s1", "Old", "unauthorized", null, [Carried]));

        await Assert.That(none.Note).IsEqualTo(HandoffOutcomeViewModel.SignInNote);
        await Assert.That(some.Note).IsEqualTo(HandoffOutcomeViewModel.SignInPartlyNote);
    }

    [Test]
    public async Task A_failure_that_carried_rows_does_not_say_nothing_was_carried() {
        var some = new HandoffOutcomeViewModel(new("s1", "Old", "failed", "takeover_error", [Carried]));
        var none = new HandoffOutcomeViewModel(new("s1", "Old", "failed", "takeover_error", []));

        await Assert.That(some.Note).IsEqualTo("Only part of the previous session's work was carried over (takeover_error).");
        await Assert.That(none.Note).IsEqualTo("Couldn't carry over the previous session's work (takeover_error).");
    }

    [Test]
    public async Task A_source_active_refusal_is_not_flagged() {
        var vm = new HandoffOutcomeViewModel(HandoffOutcomeDto.Refused("s1", "Old", "source_active"));

        await Assert.That(vm.NoteNeedsAttention).IsFalse();
    }
}
