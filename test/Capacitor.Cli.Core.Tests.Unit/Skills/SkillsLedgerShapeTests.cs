using System.Text.Json;
using Capacitor.Cli.Core.Skills;

namespace Capacitor.Cli.Core.Tests.Unit.Skills;

/// <summary>The ledger is persisted state, so the wire spelling of every state and cause is pinned
/// here: a ledger written by one build has to be read by the next.</summary>
public class SkillsLedgerShapeTests {
    [TempDir] public required TempDir Tmp { get; init; }

    static SkillDocument Document(string slug = "x") => new() {
        DocId = Guid.Empty, Slug = slug, Version = 1, ContentHash = "h", Home = "repo:owner/name",
        Applicability = new SkillApplicability { Vendors = ["claude"], FlowRoles = ["author"] },
    };

    [Test]
    public async Task A_row_round_trips_every_field_and_spells_its_state_and_cause() {
        var ledger = new SkillsLedger {
            Etag = "e1", SyncedAt = DateTimeOffset.UnixEpoch,
            Identity = new SkillsIdentity("acct-1", "https://server"),
            Exposure = ["claude", "copilot"],
            LegacyRetirement = new SkillsIdentity("acct-0", "https://server"),
            Owned = [new OwnedSkillRow {
                Path = "/repo/.claude/skills/kcap-x", Root = "/repo/.claude/skills", Anchor = "/repo",
                Origin = SkillOrigin.Repository, State = OwnedSkillState.Owed,
                Cause = SkillDeletionCause.Retired,
                IdentityRetired = new SkillsIdentity("acct-0", "https://server"),
                Confirmed = new SkillReceipt { FileHash = "f1", Document = Document() },
                Inherited = [new SkillReceipt { FileHash = "f2", Document = Document() }],
                Prepared = new PreparedSkillWrite {
                    Operation = Guid.Parse("7c9a1f02-0000-4000-8000-000000000001"),
                    Intended  = new SkillReceipt { FileHash = "f3", Document = Document() },
                    Identity  = new SkillsIdentity("acct-1", "https://server"),
                },
            }],
        };

        var json = JsonSerializer.Serialize(ledger, CapacitorJsonContext.Default.SkillsLedger);

        await Assert.That(json).Contains("\"state\":\"owed\"");
        await Assert.That(json).Contains("\"cause\":\"retired\"");
        await Assert.That(json).Contains("\"origin\":\"repository\"");

        var again = JsonSerializer.Deserialize(json, CapacitorJsonContext.Default.SkillsLedger)!;
        var row   = again.Rows.Single();

        await Assert.That(again.LegacyRetirement!.Account).IsEqualTo("acct-0");
        await Assert.That(again.Exposure).IsEquivalentTo(["claude", "copilot"]);
        await Assert.That(row.Anchor).IsEqualTo("/repo");
        await Assert.That(row.Confirmed!.Document.Home).IsEqualTo("repo:owner/name");
        await Assert.That(row.Confirmed.Document.Applicability!.Vendors).IsEquivalentTo(["claude"]);
        await Assert.That(row.Inherited!.Single().FileHash).IsEqualTo("f2");
        await Assert.That(row.Prepared!.Identity.Account).IsEqualTo("acct-1");
        await Assert.That(row.IdentityRetired!.Account).IsEqualTo("acct-0");
    }

    [Test]
    [Arguments(OwnedSkillState.Reserved, "reserved")]
    [Arguments(OwnedSkillState.Published, "published")]
    [Arguments(OwnedSkillState.Unverified, "unverified")]
    [Arguments(OwnedSkillState.Settled, "settled")]
    public async Task Each_state_keeps_its_wire_spelling(OwnedSkillState state, string spelling) {
        var ledger = new SkillsLedger {
            Owned = [new OwnedSkillRow {
                Path = "/p", Root = "/", Origin = SkillOrigin.Legacy, State = state,
                Confirmed = state == OwnedSkillState.Published
                    ? new SkillReceipt { FileHash = "f", Document = Document() } : null,
            }],
        };

        await Assert.That(JsonSerializer.Serialize(ledger, CapacitorJsonContext.Default.SkillsLedger))
            .Contains($"\"state\":\"{spelling}\"");
    }

    /// <summary>The shape every installed release wrote. Each entry converts to a published row
    /// taking its receipt from the recorded file hash, and one without a hash converts to a claim
    /// nothing can vouch for — never a published row adopting whatever is on disk.</summary>
    [Test]
    public async Task The_shipped_shape_converts_with_no_identity_and_no_anchor() {
        var path = Tmp.CreateFile("manifest.json", """
            {"etag":"etag-0","synced_at":"2026-09-17T09:00:00+00:00",
             "skills":[{"doc_id":"7c9a1f02-0000-4000-8000-000000000001","slug":"alpha","version":2,
                        "content_hash":"h","path":"/home/.claude/skills/kcap-alpha","file_hash":"f"},
                       {"doc_id":"7c9a1f02-0000-4000-8000-000000000002","slug":"beta","version":1,
                        "content_hash":"h","path":"/home/.claude/skills/kcap-beta"}]}
            """);

        await Assert.That(SkillsLedgerFile.Read(path, SkillOrigin.Legacy, out var ledger))
            .IsEqualTo(SkillsLedgerRead.Loaded);

        var rows = ledger!.Rows.ToDictionary(r => Path.GetFileName(r.Path));

        await Assert.That(ledger.Identity).IsNull();
        await Assert.That(ledger.Etag).IsEqualTo("etag-0");
        await Assert.That(rows.Count).IsEqualTo(2);
        await Assert.That(rows["kcap-alpha"].State).IsEqualTo(OwnedSkillState.Published);
        await Assert.That(rows["kcap-alpha"].Anchor).IsNull();
        await Assert.That(rows["kcap-alpha"].Origin).IsEqualTo(SkillOrigin.Legacy);
        await Assert.That(rows["kcap-alpha"].Confirmed!.FileHash).IsEqualTo("f");
        await Assert.That(rows["kcap-alpha"].Confirmed.Document.Version).IsEqualTo(2);
        await Assert.That(rows["kcap-beta"].State).IsEqualTo(OwnedSkillState.Unverified);
        await Assert.That(rows["kcap-beta"].Confirmed).IsNull();
    }

    /// <summary>A ledger that could not be reached is not one this repository stopped owning. It
    /// carries the obligation to retire what it names, and reading it as missing discharges that
    /// obligation for good.</summary>
    [Test]
    public async Task A_ledger_that_cannot_be_reached_is_unreadable_rather_than_missing() {
        Skip.When(OperatingSystem.IsWindows(), "file modes are the mechanism this inspects");

        var holder = Tmp.CreateDir("holder");
        var path   = holder.CreateFile("manifest.json", """{"owned":[]}""");

        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(holder, UnixFileMode.UserRead);

        try {
            Skip.When(new FileInfo(path).Exists, "this user is not subject to the directory's mode");

            await Assert.That(SkillsLedgerFile.Read(path, SkillOrigin.Legacy, out _))
                .IsEqualTo(SkillsLedgerRead.Unreadable);
        } finally {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(holder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Test]
    public async Task An_unreadable_shape_is_told_from_a_missing_one_and_from_a_corrupt_one() {
        await Assert.That(SkillsLedgerFile.Read(Tmp.PathTo("gone.json"), SkillOrigin.Repository, out _))
            .IsEqualTo(SkillsLedgerRead.Missing);
        await Assert.That(SkillsLedgerFile.Read(Tmp.CreateFile("bad.json", "{ truncated"),
                                                SkillOrigin.Repository, out _))
            .IsEqualTo(SkillsLedgerRead.Corrupt);
        // A parseable object in neither shape owns nothing and says nothing: same recovery route.
        await Assert.That(SkillsLedgerFile.Read(Tmp.CreateFile("empty.json", "{}"),
                                                SkillOrigin.Repository, out _))
            .IsEqualTo(SkillsLedgerRead.Corrupt);
        await Assert.That(SkillsLedgerFile.Read(Tmp.CreateFile("none.json", """{"owned":[]}"""),
                                                SkillOrigin.Repository, out _))
            .IsEqualTo(SkillsLedgerRead.Loaded);
    }
}
