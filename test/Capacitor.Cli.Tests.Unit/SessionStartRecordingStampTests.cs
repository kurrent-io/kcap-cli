using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli.Tests.Unit;

public class SessionStartRecordingStampTests {
    [TempHome]       public required TempHome       Home   { get; init; }
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }
    [TempDir]        public required TempDir        Tmp    { get; init; }

    static Func<string, string?> Env(params (string Key, string Value)[] vars) =>
        key => vars.FirstOrDefault(v => v.Key == key).Value;

    static JsonObject Body(string? transcript) =>
        transcript is null ? new JsonObject() : new JsonObject { ["transcript_path"] = transcript };

    [Test]
    public async Task Stamp_carries_this_machines_id_and_the_transcripts_store() {
        var work = Home.CreateDir(".claude-work");
        var body = Body(Home.CreateFile(".claude-work/projects/-repo/abc.jsonl", ""));

        SessionStartInventory.Stamp(body, Config.Root, TestHarnesses.Under(Home), HarnessId.Claude, TimeProvider.System);

        var recording = body["recording"]!.AsObject();
        await Assert.That((string?)recording["machine_id"]).IsEqualTo(new MachineId(Config.Root).Get());
        await Assert.That((string?)recording["config_dir"]).IsEqualTo(AccountDirectory.Normalize(work));
        await Assert.That(recording.ContainsKey("account_id")).IsFalse();
    }

    [Test]
    public async Task An_unknown_store_is_left_out_rather_than_defaulted() {
        Home.CreateDir(".claude");
        var body = Body(null);

        SessionStartInventory.Stamp(body, Config.Root, TestHarnesses.Under(Home), HarnessId.Claude, TimeProvider.System);

        await Assert.That(body["recording"]!.AsObject().ContainsKey("config_dir")).IsFalse();
    }

    [Test]
    public async Task A_registered_store_names_its_account() {
        var work     = Home.CreateDir(".claude-work");
        var accounts = new AccountStore(Tmp.PathTo("accounts"));
        accounts.Mutate(r => (r with { Accounts = [new VendorAccount("acct-1", HarnessId.Claude, AccountDirectory.Normalize(work), "Work", DateTimeOffset.UnixEpoch)] }, 0));
        var body = Body(Home.CreateFile(".claude-work/projects/-repo/abc.jsonl", ""));

        var recording = SessionStartInventory.RecordingOf(body, Config.Root, TestHarnesses.Under(Home), HarnessId.Claude, null, accounts, Env());

        await Assert.That(recording.AccountId).IsEqualTo("acct-1");
    }

    /// <summary>An adapter that builds its own body passes the vendor's transcript separately; one outside
    /// the vendor's root leaves the store unknown.</summary>
    [Test]
    public async Task A_transcript_passed_beside_the_body_is_checked_against_the_root() {
        Home.CreateDir(".gemini");
        var outside = Home.CreateFile("elsewhere/.gemini/tmp/p/chats/session.json", "");
        var body    = Body(null);

        SessionStartInventory.Stamp(body, Config.Root, TestHarnesses.Under(Home), HarnessId.Gemini, TimeProvider.System, outside);

        await Assert.That(body["recording"]!.AsObject().ContainsKey("config_dir")).IsFalse();
    }

    /// <summary>A regular file where the config directory should be fails the write for every process,
    /// whatever its privileges.</summary>
    [Test]
    public async Task A_machine_id_that_cannot_be_written_leaves_the_rest_of_the_identity() {
        var work      = Home.CreateDir(".claude-work");
        var notADir   = new ConfigRoot(Tmp.CreateFile("config-is-a-file", ""));

        var recording = SessionStartInventory.RecordingOf(
            Body(Home.CreateFile(".claude-work/projects/-repo/abc.jsonl", "")), notADir, TestHarnesses.Under(Home), HarnessId.Claude,
            null, null, Env());

        await Assert.That(recording.MachineId).IsNull();
        await Assert.That(recording.ConfigDir).IsEqualTo(AccountDirectory.Normalize(work));
    }

    [Test]
    public async Task A_daemon_hosted_session_names_its_daemon() {
        var recording = SessionStartInventory.RecordingOf(
            Body(null), Config.Root, TestHarnesses.Under(Home), HarnessId.Claude, null, null,
            Env((HostedAgent.DaemonAgentIdVar, "agent-1"), (DaemonNameResolver.NameVar, "laptop")));

        await Assert.That(recording.DaemonName).IsEqualTo("laptop");
    }

    /// <summary>A terminal may export <c>KCAP_DAEMON_NAME</c> for its own daemon; without the agent marker
    /// the daemon did not host this session.</summary>
    [Test]
    public async Task A_daemon_name_without_the_agent_marker_is_not_a_hosting_daemon() {
        var recording = SessionStartInventory.RecordingOf(
            Body(null), Config.Root, TestHarnesses.Under(Home), HarnessId.Claude, null, null, Env((DaemonNameResolver.NameVar, "laptop")));

        await Assert.That(recording.DaemonName).IsNull();
    }

    [Test]
    public async Task A_malformed_transcript_path_leaves_the_rest_of_the_body_intact() {
        var body = new JsonObject { ["transcript_path"] = 42, ["session_id"] = "s" };

        SessionStartInventory.Stamp(body, Config.Root, TestHarnesses.Under(Home), HarnessId.Claude, TimeProvider.System);

        await Assert.That((string?)body["session_id"]).IsEqualTo("s");
        await Assert.That((string?)body["recording"]!["machine_id"]).IsNotNull();
    }
}
