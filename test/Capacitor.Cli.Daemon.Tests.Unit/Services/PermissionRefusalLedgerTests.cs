using Capacitor.Cli.Core.Policy;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>The refusals one bridge declares: newest first, per session, and incomplete whenever an
/// entry was dropped or cut, or the bridge did not see the session from its start — the judge must not
/// allow while a refusal may be missing.</summary>
public class PermissionRefusalLedgerTests {
    const string Session = "s-1";

    static CanonicalAction Shell(string command) => new() { Kind = ActionKind.Shell, Vendor = "cursor", Command = command };

    static PermissionRefusalLedger Marked() {
        var ledger = new PermissionRefusalLedger();
        ledger.MarkBridgedSinceStart(Session);
        return ledger;
    }

    static void Refuse(PermissionRefusalLedger ledger, string? callId, string? tool = "execute", string command = "x",
                       string? approvalId = null, string? promptId = null, string session = Session) =>
        ledger.Record(session, callId, approvalId, tool, Shell(command), promptId);

    /// A resumed, rebound or adopted session may have refusals from before this bridge saw it.
    [Test]
    public async Task An_unmarked_session_declares_incomplete() {
        var declared = new PermissionRefusalLedger().Declare(Session);

        await Assert.That(declared.Complete).IsFalse();
        await Assert.That(declared.Source).IsEqualTo(PolicyJudgeRefusalsV1.SourceBridge);
        await Assert.That(declared.Entries).IsEmpty();
    }

    /// A request the bridge failed on before it knew the session was denied without it, in some session.
    [Test]
    public async Task A_request_lost_before_its_session_was_known_makes_every_session_incomplete() {
        var ledger = Marked();
        ledger.MarkAllIncomplete();

        await Assert.That(ledger.Declare(Session).Complete).IsFalse();
    }

    [Test]
    public async Task A_marked_session_with_no_refusals_declares_complete() =>
        await Assert.That(Marked().Declare(Session).Complete).IsTrue();

    [Test]
    public async Task Entries_are_declared_newest_first_and_per_session_with_their_prompt() {
        var ledger = Marked();
        Refuse(ledger, "call-1", command: "one", promptId: "turn-1");
        Refuse(ledger, "call-2", command: "two", promptId: "turn-2");
        Refuse(ledger, "call-3", command: "three", session: "other");

        var declared = ledger.Declare(Session);

        await Assert.That(declared.Complete).IsTrue();
        await Assert.That(declared.Entries.Select(e => e.ToolUseId).ToArray()).IsEquivalentTo(new[] { "call-2", "call-1" });
        await Assert.That(declared.Entries[0].Target).IsEqualTo("two");
        await Assert.That(declared.Entries[0].PromptId).IsEqualTo("turn-2");
    }

    /// One item can raise a command approval and then a network approval for it.
    [Test]
    public async Task Two_approvals_for_one_item_are_two_entries() {
        var ledger = Marked();
        Refuse(ledger, "item-1", approvalId: "a1");
        Refuse(ledger, "item-1", approvalId: "a2");

        var declared = ledger.Declare(Session);

        await Assert.That(declared.Complete).IsTrue();
        await Assert.That(declared.Entries.Length).IsEqualTo(2);
    }

    /// Without an approval id a second refusal of the same item cannot be told from a repeat of the first.
    [Test]
    public async Task A_repeated_item_without_an_approval_id_marks_the_session_incomplete() {
        var ledger = Marked();
        Refuse(ledger, "item-1");
        Refuse(ledger, "item-1");

        await Assert.That(ledger.Declare(Session).Complete).IsFalse();
    }

    [Test]
    public async Task More_than_thirty_two_refusals_keep_the_newest_and_are_incomplete() {
        var ledger = Marked();
        for (var i = 1; i <= 33; i++) Refuse(ledger, $"call-{i}", command: $"c{i}");

        var declared = ledger.Declare(Session);

        await Assert.That(declared.Complete).IsFalse();
        await Assert.That(declared.Entries.Length).IsEqualTo(32);
        await Assert.That(declared.Entries[0].ToolUseId).IsEqualTo("call-33");
        await Assert.That(declared.Entries[^1].ToolUseId).IsEqualTo("call-2");
    }

    [Test]
    public async Task A_target_over_the_cap_is_cut_and_incomplete() {
        var ledger = Marked();
        Refuse(ledger, "call-1", command: new string('x', 2000));

        var declared = ledger.Declare(Session);

        await Assert.That(declared.Complete).IsFalse();
        await Assert.That(declared.Entries.Single().Target.Length).IsEqualTo(1024);
    }

    [Test]
    [Arguments(null)]
    [Arguments("   ")]
    public async Task A_refusal_with_no_tool_name_is_declared_but_incomplete(string? tool) {
        var ledger = Marked();
        Refuse(ledger, "call-1", tool);

        var declared = ledger.Declare(Session);

        await Assert.That(declared.Complete).IsFalse();
        await Assert.That(declared.Entries.Single().Tool).IsEqualTo("unknown");
    }

    [Test]
    public async Task A_refusal_with_no_action_or_an_unusable_id_is_incomplete() {
        var noAction = Marked();
        noAction.Record(Session, "call-1", null, "execute", action: null, promptId: null);
        var longId = Marked();
        Refuse(longId, new string('i', 129));

        await Assert.That(noAction.Declare(Session).Complete).IsFalse();
        await Assert.That(longId.Declare(Session).Complete).IsFalse();
        await Assert.That(longId.Declare(Session).Entries).IsEmpty();
    }
}
