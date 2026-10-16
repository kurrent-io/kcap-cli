namespace Capacitor.App.Services;

/// Opens a link to one of the app server's agent pages in the app when the app can show that
/// agent, and hands every other link to the browser. An agent the app does not know yet — a launch
/// the daemon has not confirmed — still opens on the web, where it will appear.
public sealed class AgentLinkOpener(IUrlOpener browser, string? serverUrl, Func<string, bool> tryOpenAgent) : IUrlOpener {
    public void Open(string url) {
        if (AgentIdOf(url, serverUrl) is { } agentId && tryOpenAgent(agentId)) return;
        browser.Open(url);
    }

    /// The id from `{serverUrl}/agents/{id}`, ignoring query and fragment; null for any other URL.
    public static string? AgentIdOf(string url, string? serverUrl) {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var link)
            || !Uri.TryCreate(serverUrl, UriKind.Absolute, out var server)
            || link.Scheme != server.Scheme
            || !string.Equals(link.Host, server.Host, StringComparison.OrdinalIgnoreCase)
            || link.Port != server.Port)
            return null;

        var prefix = server.AbsolutePath.TrimEnd('/') + "/agents/";
        var path = link.AbsolutePath;
        if (!path.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var id = Uri.UnescapeDataString(path[prefix.Length..].TrimEnd('/'));
        return id.Length == 0 || id.Contains('/') ? null : id;
    }
}
