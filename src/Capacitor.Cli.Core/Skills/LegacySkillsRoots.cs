using Capacitor.Cli.Core.Harness.Antigravity;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Harness.Kiro;

namespace Capacitor.Cli.Core.Skills;

/// <summary>
/// The user-global skills trees kcap's own copies were written into, one per target — the only
/// roots a legacy retirement may delete from.
///
/// <para>Taken from each vendor's path type rather than derived from the repository-relative tree
/// of the same name: Kiro's whole layout moves with <c>KIRO_HOME</c> and Antigravity's Gemini tree
/// with <c>GEMINI_CLI_HOME</c>, both supported, so on a machine that sets either the home-relative
/// form names a directory nothing was ever written to.</para>
/// </summary>
public sealed class LegacySkillsRoots(UserHome home, string? kiroHome, string? geminiCliHome) {
    public static LegacySkillsRoots FromEnvironment(UserHome home) =>
        new(home,
            Environment.GetEnvironmentVariable("KIRO_HOME"),
            Environment.GetEnvironmentVariable("GEMINI_CLI_HOME"));

    public string Agents { get; } = new AgentsPaths(home).UserSkillsDir;

    /// <summary>Anchored on the user home even when <c>CLAUDE_CONFIG_DIR</c> moves the rest of
    /// Claude's layout, which is what <see cref="ClaudePaths.UserSkillsDir"/> answers.</summary>
    public string Claude { get; } = new ClaudePaths(home, null).UserSkillsDir;

    public string Kiro { get; } = new KiroPaths(home, kiroHome).SkillsDir;

    /// <summary>Shared by Gemini CLI and Antigravity, whose layout hangs off Gemini's root.
    /// </summary>
    public string Gemini { get; } = new AntigravityPaths(home, geminiCliHome).SkillsDir;
}
