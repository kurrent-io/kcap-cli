using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Antigravity;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Harness.Codex;
using Capacitor.Cli.Core.Harness.Cursor;
using Capacitor.Cli.Core.Harness.Gemini;
using Capacitor.Cli.Core.Harness.Kiro;
using Capacitor.Cli.Core.Harness.Pi;

namespace Capacitor.Cli.Core.Tests.Unit.Harness;

/// <summary>
/// The registry is the one place shared code names a vendor, so these pin what a reader of a
/// single entry cannot check for itself: that every harness is present exactly once, and that each
/// reports its own identity rather than a neighbour's.
/// </summary>
public class HarnessRegistryTests {
    [TempHome] public required TempHome Home { get; init; }

    static UserHome Nowhere => new("/nonexistent-home");

    // Bare: FromEnvironment reads every vendor override variable.
    [Test, NotInParallel]
    public async Task Every_harness_is_registered_exactly_once() {
        var ids = HarnessRegistry.FromEnvironment(Home).Select(h => h.Id).ToList();

        // Both directions: a missing vendor is a harness nothing can reach, and a duplicate is the
        // copy-paste this shape invites — a module declaring IHarness<SomeOtherVendor> compiles and
        // then reports that vendor's identity.
        await Assert.That(ids).IsEquivalentTo(Enum.GetValues<HarnessId>());
        await Assert.That(ids.Distinct().Count()).IsEqualTo(ids.Count);
    }

    [Test, NotInParallel]
    public async Task Every_harness_carries_a_label() {
        var labels = HarnessRegistry.FromEnvironment(Home).Select(h => h.Label).ToList();

        await Assert.That(labels.Any(string.IsNullOrWhiteSpace)).IsFalse();
        await Assert.That(labels.Distinct().Count()).IsEqualTo(labels.Count);
    }

    /// <summary>A vendor that answers the launch question at all answers it for the command it
    /// spawns, or a machine carrying only that CLI reads as absent. Cursor declares no launch signal
    /// and is skipped.</summary>
    [Test]
    public async Task A_declared_cli_satisfies_its_vendors_launch_signal() {
        using var bin = new TempDir();

        var stray = TestHarnesses.Under(Home)
            .Where(h => h.Signals.LaunchSignal is not null
                     && !h.Signals.CanLaunch(TestBinaries.Searching(bin, h.CliBinary)))
            .Select(h => h.Id)
            .ToList();

        await Assert.That(stray).IsEmpty();
    }

    /// <summary>Each vendor's layout comes from its own factory rather than the registry reading the
    /// override variables itself, so relocating one vendor leaves the others where they were.</summary>
    // Bare: the overrides are inherited by any child a concurrent test spawns.
    [Test, NotInParallel]
    public async Task Each_vendor_is_routed_through_its_own_override() {
        using var relocated = new TempDir();

        using var claude = EnvScope.Exclusive("CLAUDE_CONFIG_DIR", relocated.PathTo("claude"));
        using var codex  = EnvScope.Exclusive("CODEX_HOME", relocated.PathTo("codex"));
        using var gemini = EnvScope.Exclusive("GEMINI_CLI_HOME", relocated.Path);
        using var kiro   = EnvScope.Exclusive("KIRO_HOME", relocated.PathTo("kiro"));

        var harnesses = HarnessRegistry.FromEnvironment(new("/fake/home"));

        await Assert.That(harnesses.Of<ClaudeHarness>().Paths.Home).IsEqualTo(relocated.PathTo("claude"));
        await Assert.That(harnesses.Of<CodexHarness>().Paths.Home).IsEqualTo(relocated.PathTo("codex"));
        await Assert.That(harnesses.Of<GeminiHarness>().Paths.Root).IsEqualTo(relocated.PathTo(".gemini"));
        await Assert.That(harnesses.Of<KiroHarness>().Paths.ConfigRoot).IsEqualTo(relocated.PathTo("kiro"));
        // Untouched variables leave their vendor on the injected home.
        await Assert.That(harnesses.Of<PiHarness>().Paths.Root).IsEqualTo(Path.Combine("/fake/home", ".pi"));
    }

    /// <summary>Antigravity's layout hangs off Gemini's root, and the registry composes it from the
    /// same instance — the reason its module has no <c>FromEnvironment</c> of its own, and why a
    /// relocated Gemini root takes Antigravity with it.</summary>
    [Test, NotInParallel]
    public async Task Antigravity_hangs_off_the_same_gemini_root() {
        using var relocated = new TempDir();

        using var gemini = EnvScope.Exclusive("GEMINI_CLI_HOME", relocated.Path);

        var harnesses = HarnessRegistry.FromEnvironment(new("/fake/home"));

        await Assert.That(harnesses.Of<AntigravityHarness>().Paths.McpConfigJson)
            .StartsWith(harnesses.Of<GeminiHarness>().Paths.Root);
    }

    [Test]
    public async Task Resolves_the_cli_a_vendor_declares() {
        using var bin = new TempDir();
        var       probe = TestBinaries.Searching(bin, "claude");

        var harnesses = HarnessRegistry.Over(probe, TestHarnesses.Spawning(HarnessId.Claude, "claude"));

        await Assert.That(harnesses.ResolveExecutable(HarnessId.Claude)).IsEqualTo(probe.Resolve("claude"));
    }

    /// <summary>A CLI resolves even for a vendor that probes for no binary at all.</summary>
    [Test]
    public async Task Resolves_a_cli_a_vendor_never_probes_for() {
        using var bin   = new TempDir();
        var       probe = TestBinaries.Searching(bin, "cursor-agent");

        var cursor    = CursorHarness.Over(new CursorPaths(Nowhere));
        var harnesses = HarnessRegistry.Over(probe, cursor);

        await Assert.That(cursor.Signals.LaunchSignal).IsNull();
        await Assert.That(harnesses.ResolveExecutable(HarnessId.Cursor)).IsEqualTo(probe.Resolve("cursor-agent"));
    }

    /// Mirrors <see cref="HarnessRegistry.Detect"/>: an id this registry never carried reads as
    /// absent rather than throwing.
    [Test]
    public async Task An_id_this_registry_does_not_carry_yields_null() {
        var harnesses = HarnessRegistry.Over(TestBinaries.None, TestHarnesses.Spawning(HarnessId.Claude, "claude"));

        await Assert.That(harnesses.ResolveExecutable(HarnessId.Codex)).IsNull();
    }

    /// <summary>A declared name that is not the CLI is no fallback. The first assertion is the
    /// precondition: that same name does detect the vendor.</summary>
    [Test]
    public async Task A_declared_name_that_is_not_the_cli_is_no_fallback() {
        using var bin   = new TempDir();
        var       probe = TestBinaries.Searching(bin, "kiro");

        var harnesses = HarnessRegistry.Over(probe, KiroHarness.Over(new KiroPaths(Nowhere, null)));

        await Assert.That(harnesses.Detect(HarnessId.Kiro).BinaryFound).IsTrue();
        await Assert.That(harnesses.ResolveExecutable(HarnessId.Kiro)).IsNull();
    }

    /// <summary>Both of Antigravity's names on the search path: the CLI is what comes back.</summary>
    [Test]
    public async Task Antigravity_resolves_its_cli_rather_than_the_ide_launcher() {
        using var bin   = new TempDir();
        var       probe = TestBinaries.Searching(bin, "antigravity", "agy");

        var harnesses = HarnessRegistry.Over(
            probe, AntigravityHarness.Over(GeminiHarness.Over(new GeminiPaths(Nowhere, null))));

        await Assert.That(harnesses.ResolveExecutable(HarnessId.Antigravity)).IsEqualTo(probe.Resolve("agy"));
    }

    /// <summary>The same for Kiro, whose bare name is the IDE.</summary>
    [Test]
    public async Task Kiro_resolves_its_cli_rather_than_the_ide_launcher() {
        using var bin   = new TempDir();
        var       probe = TestBinaries.Searching(bin, "kiro", "kiro-cli");

        var harnesses = HarnessRegistry.Over(probe, KiroHarness.Over(new KiroPaths(Nowhere, null)));

        await Assert.That(harnesses.ResolveExecutable(HarnessId.Kiro)).IsEqualTo(probe.Resolve("kiro-cli"));
    }

    /// <summary>A redirected registry resolves on the path it was handed, not the one it was built
    /// over. The first assertion is the precondition: the original probe genuinely cannot see it.</summary>
    [Test]
    public async Task Searching_resolves_on_the_new_path() {
        using var launcher = new TempDir();
        using var shell    = new TempDir();

        var inherited = TestBinaries.Searching(launcher);
        var login     = TestBinaries.Searching(shell, "claude");

        var harnesses = HarnessRegistry.Over(inherited, TestHarnesses.Spawning(HarnessId.Claude, "claude"));

        await Assert.That(harnesses.ResolveExecutable(HarnessId.Claude)).IsNull();
        await Assert.That(harnesses.Searching(login).ResolveExecutable(HarnessId.Claude))
            .IsEqualTo(login.Resolve("claude"));
    }

    /// <summary>An entry under the right id but of another type is named, not cast blindly.</summary>
    [Test]
    public async Task Of_names_the_id_and_the_expected_type_when_the_entry_is_another_implementation() {
        var harnesses = HarnessRegistry.Over(TestBinaries.None, TestHarnesses.Of(HarnessId.Claude));

        var error = Assert.Throws<InvalidOperationException>(() => harnesses.Of<ClaudeHarness>());

        await Assert.That(error.Message).Contains(nameof(HarnessId.Claude));
        await Assert.That(error.Message).Contains(nameof(ClaudeHarness));
    }
}
