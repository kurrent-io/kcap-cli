using Capacitor.Cli.Core.Accounts;

namespace Capacitor.Tests.Helpers;

public static class TestAccounts {
    /// <summary>A store whose location does not exist and is never written: it loads as an empty registry.</summary>
    public static AccountStore None { get; } = new(Path.Combine(AppContext.BaseDirectory, "no-accounts"));
}
