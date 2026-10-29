using Capacitor.Cli.Core.Mcp;

namespace Capacitor.Cli.Core.Harness.Muse;

/// <summary>kcap's MCP servers in Muse's user <c>settings.json</c>. No <c>cwd</c>: Muse starts
/// servers in the workspace, and the servers resolve the repository from the process cwd.
/// A file without <c>schema_version</c> stops every <c>muse</c> command, so it is added first.</summary>
public static class MuseSettingsJson {
    public static JsonMcpConfigWriter.Change RegisterKcapMcpServers(string settingsPath, UserHome home, Func<string?>? resolveBinaryPath = null) {
        var versioned = JsonSettingsFile.Edit(settingsPath, root => {
            if (root["schema_version"] is not null) return false;
            root["schema_version"] = 1;
            return true;
        });
        if (versioned is SettingsEdit.Malformed or SettingsEdit.Failed) return JsonMcpConfigWriter.Change.Failed;

        return HarnessMcpProjections.Muse.Register(settingsPath, home, cwd: null, resolveBinaryPath);
    }

    public static JsonMcpConfigWriter.Change UnregisterKcapMcpServers(string settingsPath, UserHome home) =>
        HarnessMcpProjections.Muse.Unregister(settingsPath, home);

    public static bool OwnsAnything(string settingsPath, UserHome home) =>
        HarnessMcpProjections.Muse.OwnsAnything(settingsPath, home);
}
