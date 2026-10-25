using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli.Core.Accounts;

/// <summary>The environment variable that moves a vendor's config directory, and what a child launched
/// under an account gets for it.</summary>
public static class VendorHomeVariable {
    public static string? NameOf(HarnessId vendor) => vendor switch {
        HarnessId.Claude => "CLAUDE_CONFIG_DIR",
        HarnessId.Codex  => "CODEX_HOME",
        _                => null,
    };

    /// <summary>A null <c>Value</c> means remove the variable: the built-in directory is reached by not
    /// naming it, since <c>CLAUDE_CONFIG_DIR=~/.claude</c> moves <c>.claude.json</c> inside it. Null for a
    /// vendor without a variable.</summary>
    public static (string Variable, string? Value)? Plan(HarnessId vendor, string directory, UserHome home) =>
        NameOf(vendor) is { } variable
            ? (variable, AccountDirectory.Same(directory, AccountLayouts.DefaultDirectory(vendor, home)) ? null : directory)
            : null;
}
