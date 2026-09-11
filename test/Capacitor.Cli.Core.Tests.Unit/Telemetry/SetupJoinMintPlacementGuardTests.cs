using System.Runtime.CompilerServices;
using Capacitor.Cli.Core.Telemetry;

namespace Capacitor.Cli.Core.Tests.Unit.Telemetry;

/// <summary>
/// Where the join key is minted, and — because the bugs it guards are silent — that the
/// once-per-device <c>cli_first_run</c> event actually carries it.
///
/// <para>The key is minted as the facade starts, gated to the two interactive auth
/// commands (<c>setup</c>/<c>login</c>), BEFORE <c>NoticeAndFirstRun</c> captures <c>cli_first_run</c>.
/// Two placement bugs are cheap to reintroduce and produce no error at all. Mint AFTER
/// <c>cli_first_run</c> and that once-ever event ships without the key, with no later run able to
/// repair it. Mint UNCONDITIONALLY and every <c>recap</c>/<c>import</c> carries a key that correlates
/// nothing, redefining a per-auth-run id as a process id.</para>
///
/// <para>Pinned behaviourally, not by source shape: the behavioural tests capture
/// <c>cli_first_run</c> into the facade's own sink (no network, no disk), which is the only form
/// that proves the ordering rather than a proxy for it. A source enumeration backs them up, because
/// a mint added to a second file would slip past a test that only starts a facade.</para>
/// </summary>
public class SetupJoinMintPlacementGuardTests {
    [TempDir] public required TempDir Tmp { get; init; }

    // The regression this whole feature's contract turns on: cli_first_run is captured once per
    // device, as the facade starts, and for an auth run it must carry the join key. Minting after
    // NoticeAndFirstRun leaves it without one, and being once-ever, no later run repairs it.
    [Test]
    [Arguments("setup")]
    [Arguments("login")]
    public async Task cli_first_run_carries_the_join_key_for_an_auth_command(string command) {
        var config = new ConfigRoot(Tmp.Path);
        var probe  = TelemetryProbe.Start(command, config);
        TelemetryTestGuards.AssertEnabled(command, config, probe.Telemetry);

        await Assert.That(probe.Telemetry.Join.Current).IsNotNull().Because("an auth command mints the key");

        var firstRun = probe.Events.SingleOrDefault(e => e.Name == "cli_first_run");
        await Assert.That(firstRun).IsNotNull()
            .Because("a fresh device's first auth command emits cli_first_run");
        await Assert.That(firstRun!.Properties[SetupJoin.PropertyName]?.GetValue<string>())
            .IsEqualTo(probe.Telemetry.Join.Current)
            .Because("cli_first_run must carry the minted key, which requires the mint to precede NoticeAndFirstRun");
    }

    // recap is reportable, so a fresh device still emits cli_first_run for it — but it has no auth run
    // to correlate, so it must mint nothing. An unconditional mint as the facade starts is exactly what
    // this catches: it would set Current and stamp join_id onto recap/import events.
    [Test]
    public async Task A_non_auth_command_mints_no_join_key() {
        var config = new ConfigRoot(Tmp.Path);
        var probe  = TelemetryProbe.Start("recap", config);
        TelemetryTestGuards.AssertEnabled("recap", config, probe.Telemetry);

        await Assert.That(probe.Telemetry.Join.Current).IsNull().Because("a non-auth command must not mint the key");

        var firstRun = probe.Events.SingleOrDefault(e => e.Name == "cli_first_run");
        await Assert.That(firstRun).IsNotNull().Because("recap is reportable, so a fresh device still emits cli_first_run");
        await Assert.That(firstRun!.Properties.ContainsKey(SetupJoin.PropertyName)).IsFalse()
            .Because("no auth run means no join_id on the event");
    }

    // === Source backstop: the mint lives in exactly one file. ===

    // A mint in a second file is invisible to the behavioural tests above — they only start a
    // facade. Enumerating src/ catches a mint in a command handler or buried in a lane, either of
    // which mints too late (past cli_first_run) or only on the lane that happens to run.
    [Test]
    public async Task Only_CliTelemetry_mints_the_join_key() {
        var minting = FindMintingFiles(SrcRoot()).Order().ToArray();

        await Assert.That(minting).IsEquivalentTo(new[] { "CliTelemetry.cs" })
            .Because("the join key is minted once, as the facade starts; a mint anywhere else is "
                   + "either too late (past cli_first_run) or lane-dependent");
    }

    // Proves the enumeration detects a mint and ignores a commented one, against a synthetic fixture.
    [Test]
    public async Task Scanner_finds_a_real_mint_and_ignores_a_commented_one() {
        using var tmp = new TempDir();
        tmp.CreateFile("Real.cs", [
            "namespace Fixture;",
            "static class Real { static void Go() { telemetry.Join.Mint(); } }",
        ]);
        tmp.CreateFile("Documented.cs", [
            "namespace Fixture;",
            "// never Join.Mint() from here",
            "static class Documented { }",
        ]);

        await Assert.That(FindMintingFiles(tmp.Path)).IsEquivalentTo(new[] { "Real.cs" });
    }

    const string Mint = "Join.Mint()";

    static string SrcRoot() => Path.Combine(RepoRoot(), "src");

    static string RepoRoot([CallerFilePath] string here = "") {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "Capacitor.slnx")))
            dir = Path.GetDirectoryName(dir);

        if (dir is null)
            throw new InvalidOperationException($"Could not locate repo root (Capacitor.slnx) walking up from {here}");

        return dir;
    }

    // Files under root that mint in code. bin/obj are excluded — they sit inside src/ and hold
    // generated sources. A mint on a // line is documentation, not code.
    static List<string> FindMintingFiles(string root) {
        var found = new List<string>();

        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                                   .Any(segment => segment is "bin" or "obj"))) {
            var source = File.ReadAllText(file);
            var from   = 0;

            while ((from = source.IndexOf(Mint, from, StringComparison.Ordinal)) >= 0) {
                var lineStart = source.LastIndexOf('\n', from) + 1;
                if (!source[lineStart..from].Contains("//", StringComparison.Ordinal)) {
                    found.Add(Path.GetFileName(file));
                    break;
                }
                from += Mint.Length;
            }
        }

        return found;
    }
}
