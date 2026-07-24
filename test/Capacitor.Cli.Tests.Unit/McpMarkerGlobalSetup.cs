using Capacitor.Cli.Core.Mcp;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// Assembly-level safety net for MCP ownership markers (mirrors <see cref="DaemonPathsGlobalSetup"/>).
///
/// <para><see cref="McpMarker"/>'s central store is pinned under the real user profile
/// (<c>~/.kcap/mcp-markers</c>) via <c>GetFolderPath(UserProfile)</c>, which ignores a redirected
/// <c>$HOME</c>/<c>KCAP_CONFIG_DIR</c>. So any test that constructs a real <c>McpMarker</c> (the plugin
/// suites, <c>UninstallCommandTests</c>, <c>McpMarkerTests</c>) reads/writes/deletes the developer's
/// real central dir. Under parallel execution those cross-suite touches raced (AI-1294) — and they
/// pollute the real home regardless.</para>
///
/// <para>Pinning the central root to a throwaway temp dir for the whole process makes the real
/// directory unreachable regardless of test order or parallelism, with no per-test override to
/// maintain (and no <c>null</c> window that would fall back to the real dir). Marker files are keyed
/// by config-path hash, so parallel tests write distinct files here and never collide; nothing
/// enumerates the dir, so concurrent writes are safe.</para>
/// </summary>
public class McpMarkerGlobalSetup {
    internal static readonly string SharedMarkerRoot = Path.Combine(
        Path.GetTempPath(),
        "kcap-mcp-markers-tests-" + Guid.NewGuid().ToString("N")[..8]
    );

    [Before(Assembly)]
    public static void PinCentralMarkerRoot() {
        Directory.CreateDirectory(SharedMarkerRoot);
        McpMarker.OverrideCentralRootForTesting(SharedMarkerRoot);
    }

    [After(Assembly)]
    public static void CleanupCentralMarkerRoot() {
        McpMarker.OverrideCentralRootForTesting(null);
        try { Directory.Delete(SharedMarkerRoot, recursive: true); } catch { /* best effort */ }
    }
}
