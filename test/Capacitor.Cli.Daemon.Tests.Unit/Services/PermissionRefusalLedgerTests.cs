using Capacitor.Cli.Core.Policy;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>The refusals one bridge declares: newest first, per session, and incomplete whenever an
/// entry was dropped or cut — the judge must not allow while a refusal may be missing.</summary>
public class PermissionRefusalLedgerTests {
    const string Session = "s-1";

    static CanonicalAction Shell(string command) => new() { Kind = ActionKind.Shell, Vendor = "cursor", Command = command };

    [Test]
    public async Task A_session_with_no_refusals_declares_a_complete_empty_history() {
        var declared = new PermissionRefusalLedger().Declare(Session);

        await Assert.That(declared.Complete).IsTrue();
        await Assert.That(declared.Source).IsEqualTo(PolicyJudgeRefusalsV1.SourceBridge);
        await Assert.That(declared.Entries).IsEmpty();
    }

    [Test]
    public async Task Entries_are_declared_newest_first_and_per_session() {
        var ledger = new PermissionRefusalLedger();
        ledger.Record(Session, "call-1", "execute", Shell("one"));
        ledger.Record(Session, "call-2", "execute", Shell("two"));
        ledger.Record("other", "call-3", "execute", Shell("three"));

        var declared = ledger.Declare(Session);

        await Assert.That(declared.Complete).IsTrue();
        await Assert.That(declared.Entries.Select(e => e.ToolUseId).ToArray()).IsEquivalentTo(new[] { "call-2", "call-1" });
        await Assert.That(declared.Entries[0].Target).IsEqualTo("two");
    }

    [Test]
    public async Task More_than_thirty_two_refusals_keep_the_newest_and_are_incomplete() {
        var ledger = new PermissionRefusalLedger();
        for (var i = 1; i <= 33; i++) ledger.Record(Session, $"call-{i}", "execute", Shell($"c{i}"));

        var declared = ledger.Declare(Session);

        await Assert.That(declared.Complete).IsFalse();
        await Assert.That(declared.Entries.Length).IsEqualTo(32);
        await Assert.That(declared.Entries[0].ToolUseId).IsEqualTo("call-33");
        await Assert.That(declared.Entries[^1].ToolUseId).IsEqualTo("call-2");
    }

    [Test]
    public async Task A_target_over_the_cap_is_cut_and_incomplete() {
        var ledger = new PermissionRefusalLedger();
        ledger.Record(Session, "call-1", "execute", Shell(new string('x', 2000)));

        var declared = ledger.Declare(Session);

        await Assert.That(declared.Complete).IsFalse();
        await Assert.That(declared.Entries.Single().Target.Length).IsEqualTo(1024);
    }

    [Test]
    [Arguments(null)]
    [Arguments("   ")]
    public async Task A_refusal_with_no_tool_name_is_declared_but_incomplete(string? tool) {
        var ledger = new PermissionRefusalLedger();
        ledger.Record(Session, "call-1", tool, Shell("git push"));

        var declared = ledger.Declare(Session);

        await Assert.That(declared.Complete).IsFalse();
        await Assert.That(declared.Entries.Single().Tool).IsEqualTo("unknown");
    }

    [Test]
    public async Task A_refusal_with_no_action_or_an_unusable_id_is_incomplete() {
        var noAction = new PermissionRefusalLedger();
        noAction.Record(Session, "call-1", "execute", action: null);
        var longId = new PermissionRefusalLedger();
        longId.Record(Session, new string('i', 129), "execute", Shell("x"));

        await Assert.That(noAction.Declare(Session).Complete).IsFalse();
        await Assert.That(longId.Declare(Session).Complete).IsFalse();
        await Assert.That(longId.Declare(Session).Entries).IsEmpty();
    }
}
