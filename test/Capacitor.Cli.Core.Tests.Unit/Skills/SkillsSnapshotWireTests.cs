using System.Text.Json;
using Capacitor.Cli.Core.Skills;

namespace Capacitor.Cli.Core.Tests.Unit.Skills;

/// <summary>
/// The snapshot payload as the server writes it, spelled out as a literal rather than round-tripped
/// through this side's own writer: a shared wrong shape deserializes cleanly on both sides and the
/// break only reaches a real server.
/// </summary>
public class SkillsSnapshotWireTests {
    [Test]
    public async Task The_servers_snapshot_shape_deserializes() {
        const string payload = """
            {
              "repo_hash": "0123456789abcdef",
              "etag": "v1",
              "skills": [
                {
                  "doc_id": "7c9a1f02-0000-4000-8000-000000000001",
                  "slug": "retry-rules",
                  "title": "Retry rules",
                  "description": "When a call may be retried.",
                  "body": "# Retry rules\n",
                  "version": 3,
                  "content_hash": "h3",
                  "applicability": {
                    "vendors": ["claude", "codex"],
                    "platforms": null,
                    "session_kinds": ["review"],
                    "flow_roles": []
                  }
                }
              ]
            }
            """;

        var snapshot = JsonSerializer.Deserialize(payload, CapacitorJsonContext.Default.SkillsSnapshotResponse)!;
        var item     = snapshot.Skills!.Single();

        await Assert.That(snapshot.Etag).IsEqualTo("v1");
        await Assert.That(item.Slug).IsEqualTo("retry-rules");
        await Assert.That(item.Version).IsEqualTo(3);
        await Assert.That(item.Applicability!.Vendors).IsEquivalentTo(["claude", "codex"]);
        await Assert.That(item.Applicability.Platforms).IsNull();
        await Assert.That(item.Applicability.SessionKinds).IsEquivalentTo(["review"]);
        await Assert.That(item.Applicability.FlowRoles!).IsEmpty();
    }

    /// <summary>A server that omits the axis sends no applicability at all, and the repository home
    /// is always derived because the document carries none.</summary>
    [Test]
    public async Task A_snapshot_without_applicability_or_home_still_deserializes() {
        const string payload = """
            {
              "repo_hash": "0123456789abcdef",
              "etag": "v1",
              "skills": [
                {
                  "doc_id": "7c9a1f02-0000-4000-8000-000000000002",
                  "slug": "plain",
                  "title": "Plain",
                  "description": null,
                  "body": "b",
                  "version": 1,
                  "content_hash": "h1"
                }
              ]
            }
            """;

        var item = JsonSerializer.Deserialize(payload, CapacitorJsonContext.Default.SkillsSnapshotResponse)!
            .Skills!.Single();

        await Assert.That(item.Applicability).IsNull();
        await Assert.That(item.Home).IsNull();
    }

    /// <summary>A receipt records what the snapshot carried, so the document it names keeps the
    /// same shape.</summary>
    [Test]
    public async Task A_receipts_document_round_trips_its_applicability() {
        var ledger = new SkillsLedger {
            Owned = [new OwnedSkillRow {
                Path = "/repo/.claude/skills/kcap-x", Root = "/repo/.claude/skills", Anchor = "/repo",
                Origin = SkillOrigin.Repository, State = OwnedSkillState.Published,
                Confirmed = new SkillReceipt {
                    FileHash = "f",
                    Document = new SkillDocument {
                        DocId = Guid.Empty, Slug = "x", Version = 1, ContentHash = "h",
                        Applicability = new SkillApplicability { Vendors = ["claude"], FlowRoles = ["author"] },
                    },
                },
            }],
        };

        var json  = JsonSerializer.Serialize(ledger, CapacitorJsonContext.Default.SkillsLedger);
        var again = JsonSerializer.Deserialize(json, CapacitorJsonContext.Default.SkillsLedger)!;
        var reach = again.Rows.Single().Confirmed!.Document.Applicability!;

        await Assert.That(json).Contains("\"session_kinds\"");
        await Assert.That(reach.Vendors).IsEquivalentTo(["claude"]);
        await Assert.That(reach.FlowRoles).IsEquivalentTo(["author"]);
        await Assert.That(reach.Platforms).IsNull();
    }
}
