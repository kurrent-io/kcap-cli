using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Harness.Codex;

namespace Capacitor.Cli.Core.Accounts;

public static class AccountLayouts {
    public static string DefaultDirectory(HarnessId vendor, UserHome home) => vendor switch {
        HarnessId.Claude => new ClaudePaths(home, null).Home,
        HarnessId.Codex  => new CodexPaths(home, null).Home,
        _                => throw new ArgumentOutOfRangeException(nameof(vendor), vendor, "Accounts cover Claude and Codex only."),
    };

    // The default directory must stay a null override: naming ~/.claude as CLAUDE_CONFIG_DIR moves
    // .claude.json inside it, away from the ~/.claude.json Claude actually reads by default.
    public static ClaudePaths Claude(UserHome home, string directory) =>
        new(home, AccountDirectory.Same(directory, DefaultDirectory(HarnessId.Claude, home)) ? null : directory);

    public static CodexPaths Codex(UserHome home, string directory) =>
        new(home, AccountDirectory.Same(directory, DefaultDirectory(HarnessId.Codex, home)) ? null : directory);
}
