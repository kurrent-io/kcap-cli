using System.Text.Json.Nodes;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// A disposable fake user home for one test, shared across the per-harness plugin
/// test suites (Cursor, Copilot, Gemini, …) so each doesn't re-derive MCP-marker
/// isolation.
///
/// Rooted UNDER the real user profile (<see cref="Environment.SpecialFolder.UserProfile"/>),
/// because <c>McpMarker</c> classifies a config as user-scope — writing its
/// ownership marker as a sidecar next to the config rather than centrally under
/// <c>~/.kcap/mcp-markers</c> — only for configs under the profile. Rooting here
/// keeps the common case a contained sidecar on every OS (GetFolderPath(UserProfile)
/// ignores $HOME on Windows, and a redirected TEMP can sit outside the profile).
///
/// On <see cref="Dispose"/> it deletes the home AND sweeps <c>~/.kcap/mcp-markers</c>
/// for any central marker whose recorded config points under this home — covering
/// the edge where the real profile is itself a git repo (McpMarker.IsInsideRepo then
/// treats the config as non-user-scope, so the marker lands centrally). So no
/// ownership-marker state can persist past the test on any OS or repo layout, for
/// either a test's direct McpMarker calls or the production <c>plugin --&lt;harness&gt;</c>
/// paths it exercises.
///
/// Suites using this must be <c>[NotInParallel("HomeEnvVarMutation")]</c> so the
/// profile McpMarker reads stays stable underneath the test.
/// </summary>
sealed class FakeUserHome : IDisposable {
    public string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        $"kcap-test-home-{Guid.NewGuid().ToString("N")[..8]}");

    public FakeUserHome() => Directory.CreateDirectory(Path);

    public void Dispose() {
        SweepOwnedCentralMarkers();
        try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
    }

    // Central markers live at ~/.kcap/mcp-markers/<harness>-<hash>.json with a
    // "config" field recording the absolute config path they own. Remove any whose
    // config is under this home (the repo-profile edge); sidecar markers under Path
    // are already removed with the directory.
    void SweepOwnedCentralMarkers() {
        var markersDir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".kcap", "mcp-markers");
        if (!Directory.Exists(markersDir)) return;

        var homePrefix = System.IO.Path.GetFullPath(Path) + System.IO.Path.DirectorySeparatorChar;
        foreach (var file in Directory.EnumerateFiles(markersDir, "*.json")) {
            try {
                if (JsonNode.Parse(File.ReadAllText(file)) is not JsonObject marker) continue;
                var config = (string?)marker["config"];
                if (config is null) continue;
                if (System.IO.Path.GetFullPath(config).StartsWith(homePrefix, StringComparison.Ordinal))
                    File.Delete(file);
            } catch { /* ignore unreadable/foreign markers */ }
        }
    }
}
