using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli.Commands;

/// <summary>Which detected vendors can answer the eval-watch prompt: the skill must be where that
/// vendor reads skills, the same oracle the guided-tour offer uses.</summary>
internal static class HandoffVendorEligibility {
    public const string SkillName = "eval-watch";

    /// <param name="installing">Harnesses an install still to run will give the skill — the browser leg
    /// decides before setup installs anything.</param>
    public static IReadOnlyList<HandoffVendor> Eligible(
            HarnessRegistry harnesses, CodingAgentsStep.Paths paths, Func<HarnessId, bool>? installing = null) => [
        .. HarnessRegistry.Identities
            .Where(i => harnesses.Detected(i.Id) && (HasSkill(i.Id, paths) || installing?.Invoke(i.Id) == true))
            .Select(i => new HandoffVendor(i.Id, i.Label, harnesses.ResolveExecutable(i.Id)))
    ];

    public static int Detected(HarnessRegistry harnesses) => HarnessRegistry.Identities.Count(i => harnesses.Detected(i.Id));

    static bool HasSkill(HarnessId id, CodingAgentsStep.Paths paths) => id switch {
        HarnessId.Claude      => SetupCommand.ClaudeCarriesSkill(paths.ClaudeSettingsPath, paths.PluginDir, SkillName),
        HarnessId.Kiro        => AgentsSkillsInstaller.HasSkill(paths.KiroSkillsDir, SkillName),
        HarnessId.Antigravity => AgentsSkillsInstaller.HasSkill(paths.AntigravitySkillsDir, SkillName),
        _                     => AgentsSkillsInstaller.HasSkill(paths.AgentsSkillsDir, SkillName),
    };
}
