namespace Capacitor.Cli.Core.Harness.Kiro;

/// <summary>
/// Filesystem layout for Kiro Crew, the desktop app that runs <c>kiro-cli acp</c> for each chat and
/// sub-agent under profiles it generates. Crew resets those profiles' hooks at every gateway start,
/// keeping only the executable scripts in <see cref="HooksDir"/>, and reads skills only from
/// <see cref="SkillsDir"/>.
/// </summary>
public sealed class KiroCrewPaths {
    /// <param name="kiroRoot">The <c>~/.kiro</c> Crew resolves from the user's home.</param>
    /// <param name="crewHome"><c>KIROCREW_HOME</c>, when set.</param>
    public KiroCrewPaths(string kiroRoot, string? crewHome) {
        Root     = !string.IsNullOrEmpty(crewHome) ? crewHome : Path.Combine(kiroRoot, "crew");
        HooksDir = Path.Combine(kiroRoot, "hooks");
    }

    /// <summary>Crew's state root (<c>~/.kiro/crew</c>), or <c>KIROCREW_HOME</c> when set.</summary>
    public string Root { get; }

    /// <summary>
    /// Crew's default hook-script directory (<c>~/.kiro/hooks</c>), fixed under the user's home rather
    /// than <c>KIRO_HOME</c>. A user who points Crew's <c>agent.kiro_hooks_dir</c> elsewhere is not
    /// covered.
    /// </summary>
    public string HooksDir { get; }

    /// <summary>kcap's <c>agentSpawn</c> script, which Crew merges into every profile it generates.</summary>
    public string SpawnHookScript => Path.Combine(HooksDir, "kcap-spawn.sh");

    /// <summary>The skills root Crew reads (<c>&lt;root&gt;/skills/&lt;name&gt;/SKILL.md</c>).</summary>
    public string SkillsDir => Path.Combine(Root, "skills");

    /// <summary>Whether Crew has run here — it creates its root on first launch.</summary>
    public bool IsPresent() => Directory.Exists(Root);
}
