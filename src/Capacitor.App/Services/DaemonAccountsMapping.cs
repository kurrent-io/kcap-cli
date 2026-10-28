using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Accounts.Readings;
using Capacitor.Remote.Models;

namespace Capacitor.App.Services;

public static class DaemonAccountsMapping {
    extension(DaemonAccountsDto row) {
        /// The server's row in the shape the daemon's own snapshot has, so one reader serves both
        /// paths. Absent arrays read as empty.
        public AccountsSnapshotDto ToSnapshot() => new(
            row.HostId, row.Revision,
            [.. (row.Accounts ?? []).Select(ToView)],
            [.. (row.Defaults ?? []).Select(d => new DefaultDirectoryDto(d.Vendor, d.Directory, d.DirectoryKey))]);
    }

    static AccountViewDto ToView(ListedAccountDto a) => new(
        a.Id, a.Vendor, a.Directory, a.DirectoryKey, a.Label, a.RecordingState,
        a.Email, a.OrgId, a.OrgName, a.Plan, a.VendorAccountId, a.SignIn,
        a.Generation, a.RefreshedAt, a.RefreshError, a.LimitsError,
        a.Reading is { } r ? ToReading(r) : null);

    static AccountReadingDto ToReading(ListedReadingDto r) => new(
        r.Generation,
        [.. (r.Windows ?? []).Where(w => w.LimitId is not null)
            .Select(w => new UsageWindowDto(w.LimitId!, w.WindowMinutes, w.UsedPercent, w.ResetsAt, w.ConfirmedAt))],
        r.Plan,
        r.Credits is { } c ? new CreditsDto(c.HasCredits, c.Unlimited, c.Balance) : null,
        r.Blocked is { } b
            ? new BlockedMarkerDto(b.BlockedAt, b.ClearedAt,
                [.. (b.Constraints ?? []).Select(k => new BlockConstraintDto(k.LimitId, k.WindowMinutes, k.ResetsAt, k.SignaledAt))])
            : null,
        r.MetadataAt);
}
