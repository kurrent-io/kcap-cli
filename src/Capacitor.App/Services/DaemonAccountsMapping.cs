using Capacitor.Cli.Core.Accounts;
using Capacitor.Remote.Models;

namespace Capacitor.App.Services;

public static class DaemonAccountsMapping {
    extension(DaemonAccountsDto row) {
        /// The server's row in the shape the daemon's own snapshot has, so one reader serves both
        /// paths. Absent arrays read as empty.
        public AccountsSnapshotDto ToSnapshot() => new(
            row.HostId, row.Revision,
            [.. (row.Accounts ?? []).Select(a => new AccountViewDto(a.Id, a.Vendor, a.Directory, a.DirectoryKey, a.Label, a.RecordingState))],
            [.. (row.Defaults ?? []).Select(d => new DefaultDirectoryDto(d.Vendor, d.Directory, d.DirectoryKey))]);
    }
}
