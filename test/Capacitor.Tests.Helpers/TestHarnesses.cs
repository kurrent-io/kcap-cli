using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Antigravity;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Harness.Codex;
using Capacitor.Cli.Core.Harness.Copilot;
using Capacitor.Cli.Core.Harness.Cursor;
using Capacitor.Cli.Core.Harness.Gemini;
using Capacitor.Cli.Core.Harness.Kiro;
using Capacitor.Cli.Core.Harness.OpenCode;
using Capacitor.Cli.Core.Harness.Pi;
using Capacitor.Cli.Core.Setup;

namespace Capacitor.Tests.Helpers;

/// <summary>
/// A harness whose answers are stated outright, for the mapping code — the nudge predicate, the
/// inventory, the first-run report — which needs an id and two answers, never a vendor's files.
/// </summary>
public sealed record TestHarness(HarnessId Id, string Label, HarnessSignals Signals) : IHarness {
    /// <summary>Blank — a name nothing resolves — unless a test says otherwise.</summary>
    public string CliBinary { get; init; } = "";
}

public static class TestHarnesses {
    /// <summary>Every harness this build knows, in registry order, detected and wired only where
    /// named. Detection comes off the marker signal, so nothing has to be staged on PATH.</summary>
    public static HarnessRegistry All(HarnessId[]? detected = null, HarnessId[]? wired = null) =>
        HarnessRegistry.Over(
            TestBinaries.None,
            [.. HarnessRegistry.Identities.Select(i =>
                Of(i.Id, detected?.Contains(i.Id) ?? false, wired?.Contains(i.Id) ?? false))]);

    /// <summary>
    /// Every real vendor rooted at <paramref name="home"/>, with no override variable consulted —
    /// the layouts a machine with a bare environment resolves. Nothing is found on the search path,
    /// so detection rests on the markers under that home and a vendor installed on the developer's
    /// own machine cannot leak into a result.
    /// </summary>
    /// <remarks>Cursor takes the host's platform, as its own factory does; a test pinning another
    /// OS's layout passes its own <see cref="CursorPaths"/>.</remarks>
    public static HarnessRegistry Under(UserHome home) => Under(home, TestBinaries.None);

    /// <summary>The same layouts, looking for vendor binaries on <paramref name="binaries"/> — what a
    /// test staging a fake CLI on PATH needs, since the hermetic default finds nothing.</summary>
    public static HarnessRegistry Under(UserHome home, BinaryProbe binaries) {
        // Antigravity's whole layout hangs off Gemini's root, so it is composed from the SAME
        // instance, as the production registry does.
        var gemini = GeminiHarness.Over(new GeminiPaths(home, null));

        return HarnessRegistry.Over(binaries,
            ClaudeHarness.Over(new ClaudePaths(home, null)),
            CodexHarness.Over(new CodexPaths(home, null)),
            CursorHarness.Over(new CursorPaths(home)),
            CopilotHarness.Over(new CopilotPaths(home, null)),
            gemini,
            KiroHarness.Over(new KiroPaths(home, null)),
            PiHarness.Over(new PiPaths(home, null)),
            OpenCodeHarness.Over(new OpenCodePaths(home, null, null, null)),
            AntigravityHarness.Over(gemini));
    }

    /// <summary>One harness that answers from the search path alone — no run signal, whatever names
    /// it declares.</summary>
    public static TestHarness Probing(HarnessId id, params string[] binaries) =>
        new(id, HarnessRegistry.LabelOf(id),
            new HarnessSignals { LaunchSignal = probe => binaries.Any(probe.Finds) });

    /// <summary>One harness declaring a spawnable CLI and no detection signal.</summary>
    public static TestHarness Spawning(HarnessId id, string cliBinary) =>
        new(id, HarnessRegistry.LabelOf(id), new HarnessSignals()) { CliBinary = cliBinary };

    /// <summary>One harness, both its answers fixed.</summary>
    public static TestHarness Of(HarnessId id, bool detected = false, bool wired = false) =>
        new(id, HarnessRegistry.LabelOf(id), new HarnessSignals {
            UserDataSignal = () => detected,
            Wired          = () => wired,
        });

}
