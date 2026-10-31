using System.Collections;
using Capacitor.Cli.Core.Harness.Antigravity;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Harness.Codex;
using Capacitor.Cli.Core.Harness.Copilot;
using Capacitor.Cli.Core.Harness.Cursor;
using Capacitor.Cli.Core.Harness.Gemini;
using Capacitor.Cli.Core.Harness.Grok;
using Capacitor.Cli.Core.Harness.Kiro;
using Capacitor.Cli.Core.Harness.MistralVibe;
using Capacitor.Cli.Core.Harness.Muse;
using Capacitor.Cli.Core.Harness.OpenCode;
using Capacitor.Cli.Core.Harness.Pi;
using Capacitor.Cli.Core.Setup;

namespace Capacitor.Cli.Core.Harness;

/// <summary>
/// The harnesses this process can see, in setup and display order, and the search path their
/// binaries are looked for on. The one place shared code names a vendor: adding one is a directory
/// plus a line here, and every consumer that treats vendors alike keeps iterating.
///
/// <para>Built once at an entry point and passed down, so two consumers cannot resolve one vendor's
/// root differently — and Antigravity is composed from the Gemini instance rather than from the
/// environment, so the root they share is read once.</para>
///
/// <para><b>No answer is cached.</b> Each detection question stats the disk when asked, so a
/// long-lived holder — the daemon keeps one for its lifetime — sees a vendor installed after it was
/// built. A snapshot is the caller's to take, where it needs one. What IS fixed at build time is
/// where to look: the search path and the vendor roots are read from the environment once, so a
/// holder does not see those variables change under it.</para>
/// </summary>
public sealed class HarnessRegistry : IReadOnlyList<IHarness>, IHarnessDetection {
    readonly IReadOnlyList<IHarness> _harnesses;
    readonly BinaryProbe             _binaries;
    readonly DesktopAppProbe         _apps;

    HarnessRegistry(IReadOnlyList<IHarness> harnesses, BinaryProbe binaries, DesktopAppProbe apps) {
        _harnesses = harnesses;
        _binaries  = binaries;
        _apps      = apps;
    }

    /// <summary>Every harness's identity, reached through each type's own statics rather than an
    /// instance — the answer for a caller with no home to build one from.</summary>
    public static IReadOnlyList<HarnessIdentity> Identities { get; } = [
        IdentityOf<ClaudeHarness>(),
        IdentityOf<CodexHarness>(),
        IdentityOf<CursorHarness>(),
        IdentityOf<CopilotHarness>(),
        IdentityOf<GeminiHarness>(),
        IdentityOf<KiroHarness>(),
        IdentityOf<PiHarness>(),
        IdentityOf<OpenCodeHarness>(),
        IdentityOf<AntigravityHarness>(),
        IdentityOf<MistralVibeHarness>(),
        IdentityOf<GrokHarness>(),
        IdentityOf<MuseHarness>(),
    ];

    static HarnessIdentity IdentityOf<TSelf>() where TSelf : IHarness<TSelf> => new(TSelf.Id, TSelf.Label);

    /// <summary>Every vendor as this machine's environment resolves it, searched over this process's
    /// own PATH.</summary>
    public static HarnessRegistry FromEnvironment(UserHome home) =>
        FromEnvironment(home, BinaryProbe.FromEnvironment());

    /// <summary>Every vendor as this machine's environment resolves it, searched over
    /// <paramref name="binaries"/> — the composition root's own DI singleton, so this registry and
    /// every other consumer of that singleton search the same path.</summary>
    public static HarnessRegistry FromEnvironment(UserHome home, BinaryProbe binaries) {
        var gemini = GeminiHarness.FromEnvironment(home);

        return new([
            ClaudeHarness.FromEnvironment(home),
            CodexHarness.FromEnvironment(home),
            CursorHarness.FromEnvironment(home),
            CopilotHarness.FromEnvironment(home),
            gemini,
            KiroHarness.FromEnvironment(home),
            PiHarness.FromEnvironment(home),
            OpenCodeHarness.FromEnvironment(home),
            AntigravityHarness.Over(gemini),
            MistralVibeHarness.FromEnvironment(home),
            GrokHarness.FromEnvironment(home),
            MuseHarness.FromEnvironment(home),
        ], binaries, DesktopAppProbe.FromEnvironment(home));
    }

    /// <summary>Over harnesses resolved elsewhere — a test's, or a subset.</summary>
    public static HarnessRegistry Over(BinaryProbe binaries, params IHarness[] harnesses) =>
        new(harnesses, binaries, DesktopAppProbe.None);

    /// <summary>Over harnesses resolved elsewhere, with desktop apps looked for under <paramref name="apps"/>.</summary>
    public static HarnessRegistry Over(BinaryProbe binaries, DesktopAppProbe apps, params IHarness[] harnesses) =>
        new(harnesses, binaries, apps);

    /// <summary>The same vendors, looked for on another search path. What a GUI launch needs: it
    /// inherits only the launcher's PATH, and spawns through the login shell's.</summary>
    public HarnessRegistry Searching(BinaryProbe binaries) => new(_harnesses, binaries, _apps);

    /// <summary>What this label says, for a caller holding an id rather than a harness.</summary>
    public static string LabelOf(HarnessId id) => Identities.First(i => i.Id == id).Label;

    /// <summary>The harness that id names. A registry missing one is a bug, not an input error —
    /// <see cref="ById"/> is what takes an id from outside this process.</summary>
    public IHarness this[HarnessId id] =>
        ById(id) ?? throw new ArgumentOutOfRangeException(nameof(id), id, "No such harness in this registry.");

    /// <summary>The one entry whose id matches, or null when the caller was handed an id from
    /// outside this process (a server payload, a flag).</summary>
    public IHarness? ById(HarnessId id) => _harnesses.FirstOrDefault(h => h.Id == id);

    /// <summary>One vendor's own harness, for code that reads that vendor's files and needs its
    /// typed layout. A registry holding another implementation under that id throws naming both,
    /// rather than an unattributed <see cref="InvalidCastException"/>.</summary>
    public TSelf Of<TSelf>() where TSelf : IHarness<TSelf> =>
        this[TSelf.Id] is TSelf harness
            ? harness
            : throw new InvalidOperationException($"Harness {TSelf.Id} in this registry is not a {typeof(TSelf).Name}.");

    /// <summary>What each detection signal says about this vendor, asked now. They stay apart: the
    /// setup screens name the one they saw.</summary>
    public DetectedAgent Detect(HarnessId id) =>
        ById(id) is { } harness
            ? new DetectedAgent(
                BinaryFound:        harness.Signals.CanLaunch(_binaries),
                InstallSignalFound: harness.Signals.HasUserData,
                DesktopAppFound:    harness.Signals.HasDesktopApp(_apps))
            // An id this registry never carried — a stale flag, a server payload — reads as absent
            // rather than throwing: neither must take a command down.
            : DetectedAgent.None;

    /// <summary>Whether any signal found this vendor.</summary>
    public bool Detected(HarnessId id) => Detect(id).Detected;

    /// <summary>Full path to <paramref name="id"/>'s <see cref="IHarness.CliBinary"/> on this
    /// registry's search path, or null. Detection signals are not consulted: a machine with the Kiro
    /// IDE and no <c>kiro-cli</c> must resolve to null, never to a GUI launcher.</summary>
    public string? ResolveExecutable(HarnessId id) =>
        ById(id) is { } harness ? _binaries.Resolve(harness.CliBinary) : null;

    /// <summary><see cref="ResolveExecutable"/>, else the CLI the vendor's desktop app bundles. For a
    /// caller about to spawn the CLI; one comparing search paths keeps to the PATH-only answer.</summary>
    public string? ResolveLaunchable(HarnessId id) =>
        ResolveExecutable(id) ?? ById(id)?.Signals.FindBundledCli(_apps);

    /// <summary>
    /// What to spawn for a configured <paramref name="command"/>: the command itself, unless it is the
    /// vendor's bare default name and that is not on the search path, when the desktop app's bundled
    /// CLI stands in. A path the operator configured is never second-guessed. Asked per launch, since
    /// a bundled CLI's path can change with each app update.
    /// </summary>
    public string LaunchCommand(HarnessId id, string command) =>
        ById(id) is { } harness
     && string.Equals(command, harness.CliBinary, StringComparison.Ordinal)
     && !_binaries.Finds(command)
     && harness.Signals.FindBundledCli(_apps) is { } bundled
            ? bundled
            : command;

    public int      Count           => _harnesses.Count;
    public IHarness this[int index] => _harnesses[index];

    public IEnumerator<IHarness> GetEnumerator() => _harnesses.GetEnumerator();
    IEnumerator IEnumerable.      GetEnumerator() => GetEnumerator();
}
