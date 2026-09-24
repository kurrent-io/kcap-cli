using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Harness.Kiro;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// `plugin install/remove --kiro` wires Kiro Crew when it is present: a hook script where Crew imports
/// hooks, and kcap's skills where Crew reads skills. The kcap agent is pre-seeded, so no install here
/// needs kiro-cli to clone it.
/// </summary>
public class PluginCommandKiroCrewTests {
    [TempHome] public required TempHome Home   { get; init; }
    [TempDir]  public required TempDir  BinDir { get; init; }

    PluginEnvironment Env() {
        var binaries = TestBinaries.Searching(BinDir, "kcap");

        return new PluginEnvironment(
            Home:              new(Home.Path),
            Profiles:          new ProfileConfig(),
            ResolvePluginPath: RepoTree.KcapDir,
            Stdout:            TextWriter.Null,
            Stderr:            TextWriter.Null
        ) {
            Harnesses            = TestHarnesses.Under(new(Home.Path), binaries),
            Binaries             = binaries,
            ResolveMcpBinaryPath = () => Path.Combine(BinDir.Path, "kcap")
        };
    }

    static void SeedAgent(PluginEnvironment env) {
        var agent = env.Harnesses.Of<KiroHarness>().Paths.KcapAgentJson;
        Directory.CreateDirectory(Path.GetDirectoryName(agent)!);
        File.WriteAllText(agent, """{"name":"kcap","hooks":{}}""");
        KiroHooksInstaller.WriteMarker(agent, "kiro_default");
    }

    static Task<int> Run(PluginEnvironment env, params string[] args) =>
        new PluginCommand(env, workdir: new WorkingDirectory(AppContext.BaseDirectory)).HandleAsync(args);

    [Test]
    public async Task Install_with_crew_present_writes_the_hook_and_the_crew_skills() {
        if (OperatingSystem.IsWindows()) return;

        var env  = Env();
        var crew = env.Harnesses.Of<KiroHarness>().Crew;
        SeedAgent(env);
        Directory.CreateDirectory(crew.Root);

        await Assert.That(await Run(env, "plugin", "install", "--kiro")).IsEqualTo(0);

        await Assert.That(KiroCrewHookInstaller.IsInstalled(crew.SpawnHookScript)).IsTrue();
        await Assert.That(await File.ReadAllTextAsync(crew.SpawnHookScript)).Contains(BinDir.Path);
        foreach (var name in AgentsSkillsInstaller.SourceNames)
            await Assert.That(AgentsSkillsInstaller.HasSkill(crew.SkillsDir, name)).IsTrue().Because($"kcap-{name} should reach Crew");
    }

    [Test]
    public async Task Install_without_crew_touches_nothing_of_crews() {
        var env  = Env();
        var crew = env.Harnesses.Of<KiroHarness>().Crew;
        SeedAgent(env);

        await Assert.That(await Run(env, "plugin", "install", "--kiro")).IsEqualTo(0);

        await Assert.That(Directory.Exists(crew.HooksDir)).IsFalse();
        await Assert.That(Directory.Exists(crew.Root)).IsFalse();
    }

    /// <summary>Crew is often installed after kcap; the npm-upgrade refresh is what reaches it.</summary>
    [Test]
    public async Task Refresh_wires_a_crew_installed_after_kiro() {
        if (OperatingSystem.IsWindows()) return;

        var env  = Env();
        var crew = env.Harnesses.Of<KiroHarness>().Crew;
        SeedAgent(env);
        await Run(env, "plugin", "install", "--kiro");
        Directory.CreateDirectory(crew.Root);

        await Assert.That(await Run(env, "plugin", "install", "--kiro", "--if-installed")).IsEqualTo(0);

        await Assert.That(KiroCrewHookInstaller.IsInstalled(crew.SpawnHookScript)).IsTrue();
        await Assert.That(AgentsSkillsInstaller.IsInstalled(crew.SkillsDir)).IsTrue();
    }

    [Test]
    public async Task Refresh_does_not_give_crew_skills_the_user_removed_from_kiro() {
        if (OperatingSystem.IsWindows()) return;

        var env  = Env();
        var kiro = env.Harnesses.Of<KiroHarness>();
        SeedAgent(env);
        Directory.CreateDirectory(kiro.Crew.Root);

        await Assert.That(await Run(env, "plugin", "install", "--kiro", "--if-installed")).IsEqualTo(0);

        await Assert.That(AgentsSkillsInstaller.IsInstalled(kiro.Paths.SkillsDir)).IsFalse();
        await Assert.That(Directory.Exists(kiro.Crew.SkillsDir)).IsFalse();
    }

    [Test]
    public async Task Remove_deletes_the_hook_and_the_crew_skills() {
        if (OperatingSystem.IsWindows()) return;

        var env  = Env();
        var crew = env.Harnesses.Of<KiroHarness>().Crew;
        SeedAgent(env);
        Directory.CreateDirectory(crew.Root);
        await Run(env, "plugin", "install", "--kiro");

        await Assert.That(await Run(env, "plugin", "remove", "--kiro")).IsEqualTo(0);

        await Assert.That(File.Exists(crew.SpawnHookScript)).IsFalse();
        foreach (var name in AgentsSkillsInstaller.SourceNames)
            await Assert.That(AgentsSkillsInstaller.HasSkill(crew.SkillsDir, name)).IsFalse();
    }
}
