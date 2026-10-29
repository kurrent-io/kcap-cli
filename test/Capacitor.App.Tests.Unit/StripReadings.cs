using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Accounts.Readings;
using Capacitor.Remote.Models;

namespace Capacitor.App.Tests.Unit;

/// Account readings for the composer strip's tests: one Claude account, "acc1", on a host.
static class StripReadings {
    public static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    public const string Directory = "/home/me/.claude-work";

    public static UsageWindowDto Window(int minutes, double used, TimeSpan resetsIn) =>
        new("claude", minutes, used, Now + resetsIn, Now);

    public static AccountReadingDto Reading(params UsageWindowDto[] windows) => new(1, windows, null, null, null);

    public static AccountReadingDto Blocked(TimeSpan resetsIn, params UsageWindowDto[] windows) =>
        new(1, windows, null, null, new BlockedMarkerDto(Now.AddMinutes(-5), null, [new("claude", 300, Now + resetsIn, Now.AddMinutes(-5))]));

    public static AccountViewDto Account(string id, AccountReadingDto? reading) =>
        new(id, "claude", Directory, Directory, "work", "recording", SignIn: SignInStates.SignedIn, Reading: reading);

    public static AccountsSnapshotDto Snapshot(params AccountViewDto[] accounts) => new("h1", 1, accounts, []);

    public static DaemonAccountsDto Remote(string daemonName, AccountReadingDto reading) => new() {
        DaemonName = daemonName, HostId = "h-" + daemonName, Revision = 1,
        Accounts = [new ListedAccountDto {
            Id = "acc1", Vendor = "claude", Directory = Directory, DirectoryKey = Directory, Label = "work", RecordingState = "recording",
            SignIn = SignInStates.SignedIn,
            Reading = new ListedReadingDto {
                Windows = [.. reading.Windows.Select(w => new ListedUsageWindowDto {
                    LimitId = w.LimitId, WindowMinutes = w.WindowMinutes, UsedPercent = w.UsedPercent, ResetsAt = w.ResetsAt, ConfirmedAt = w.ConfirmedAt,
                })],
            },
        }],
    };
}
