using Capacitor.App.Services;
using Capacitor.Remote.Models;

namespace Capacitor.App.Tests.Unit;

public class DaemonAccountsMappingTests {
    static readonly DateTimeOffset T0 = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task A_full_server_row_maps_member_for_member() {
        var row = new DaemonAccountsDto {
            DaemonName = "box", HostId = "h1", Revision = 3,
            Accounts = [new ListedAccountDto {
                Id = "a1", Vendor = "claude", Directory = "/u/.claude", DirectoryKey = "/u/.claude", Label = "work", RecordingState = "recording",
                Email = "a@b.c", OrgId = "o1", OrgName = "Org", Plan = "max", VendorAccountId = "v1", SignIn = "oauth",
                Generation = 4, RefreshedAt = T0, RefreshError = "re", LimitsError = "le",
                Reading = new ListedReadingDto {
                    Generation = 4, Plan = "max", MetadataAt = T0.AddMinutes(1),
                    Windows = [
                        new ListedUsageWindowDto { LimitId = "claude", WindowMinutes = 300, UsedPercent = 42.5, ResetsAt = T0.AddHours(1), ConfirmedAt = T0 },
                        new ListedUsageWindowDto { LimitId = null, WindowMinutes = 60, UsedPercent = 1 },
                    ],
                    Credits = new ListedCreditsDto { HasCredits = true, Unlimited = false, Balance = "12.50" },
                    Blocked = new ListedBlockedMarkerDto {
                        BlockedAt = T0, ClearedAt = T0.AddHours(2),
                        Constraints = [new ListedBlockConstraintDto { LimitId = "claude", WindowMinutes = 300, ResetsAt = T0.AddHours(1), SignaledAt = T0 }],
                    },
                },
            }],
        };

        var a = row.ToSnapshot().Accounts.Single();

        await Assert.That(a.Id).IsEqualTo("a1");
        await Assert.That(a.Label).IsEqualTo("work");
        await Assert.That(a.RecordingState).IsEqualTo("recording");
        await Assert.That(a.Email).IsEqualTo("a@b.c");
        await Assert.That(a.OrgId).IsEqualTo("o1");
        await Assert.That(a.OrgName).IsEqualTo("Org");
        await Assert.That(a.Plan).IsEqualTo("max");
        await Assert.That(a.VendorAccountId).IsEqualTo("v1");
        await Assert.That(a.SignIn).IsEqualTo("oauth");
        await Assert.That(a.Generation).IsEqualTo(4L);
        await Assert.That(a.RefreshedAt).IsEqualTo(T0);
        await Assert.That(a.RefreshError).IsEqualTo("re");
        await Assert.That(a.LimitsError).IsEqualTo("le");

        var reading = a.Reading!;
        await Assert.That(reading.Generation).IsEqualTo(4L);
        await Assert.That(reading.Plan).IsEqualTo("max");
        await Assert.That(reading.MetadataAt).IsEqualTo(T0.AddMinutes(1));
        var window = reading.Windows.Single();
        await Assert.That(window.LimitId).IsEqualTo("claude");
        await Assert.That(window.WindowMinutes).IsEqualTo(300);
        await Assert.That(window.UsedPercent).IsEqualTo(42.5);
        await Assert.That(window.ResetsAt).IsEqualTo(T0.AddHours(1));
        await Assert.That(window.ConfirmedAt).IsEqualTo(T0);
        await Assert.That(reading.Credits!.HasCredits).IsTrue();
        await Assert.That(reading.Credits.Unlimited).IsFalse();
        await Assert.That(reading.Credits.Balance).IsEqualTo("12.50");
        await Assert.That(reading.Blocked!.BlockedAt).IsEqualTo(T0);
        await Assert.That(reading.Blocked.ClearedAt).IsEqualTo(T0.AddHours(2));
        var constraint = reading.Blocked.Constraints.Single();
        await Assert.That(constraint.LimitId).IsEqualTo("claude");
        await Assert.That(constraint.WindowMinutes).IsEqualTo(300);
        await Assert.That(constraint.ResetsAt).IsEqualTo(T0.AddHours(1));
        await Assert.That(constraint.SignaledAt).IsEqualTo(T0);
    }

    [Test]
    public async Task An_old_shape_row_maps_with_nulls() {
        var row = new DaemonAccountsDto {
            DaemonName = "box", HostId = "h1", Revision = 1,
            Accounts = [new ListedAccountDto {
                Id = "a1", Vendor = "codex", Directory = "/u/.codex", DirectoryKey = "/u/.codex", Label = "main", RecordingState = "installed",
            }],
        };

        var a = row.ToSnapshot().Accounts.Single();

        await Assert.That(a.Email).IsNull();
        await Assert.That(a.OrgId).IsNull();
        await Assert.That(a.OrgName).IsNull();
        await Assert.That(a.Plan).IsNull();
        await Assert.That(a.VendorAccountId).IsNull();
        await Assert.That(a.SignIn).IsNull();
        await Assert.That(a.Generation).IsEqualTo(0L);
        await Assert.That(a.RefreshedAt).IsNull();
        await Assert.That(a.RefreshError).IsNull();
        await Assert.That(a.LimitsError).IsNull();
        await Assert.That(a.Reading).IsNull();
    }
}
