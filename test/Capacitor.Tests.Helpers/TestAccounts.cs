using Capacitor.Cli.Core.Accounts;

namespace Capacitor.Tests.Helpers;

public static class TestAccounts {
    /// <summary>A store that loads as an empty registry and fails any write with ENOTDIR: its directory sits
    /// under this assembly's own file, so a test that mutates it by mistake fails instead of leaving a
    /// registry behind for the next.</summary>
    public static AccountStore None { get; } = new(Path.Combine(typeof(TestAccounts).Assembly.Location, "accounts"));
}
