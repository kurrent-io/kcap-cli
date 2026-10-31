using Capacitor.Cli.Core.Pages;

namespace Capacitor.App.Services;

/// Names the page a link points at, but only on the profile's own server: PageReference reads an
/// id from any host, and an id from another host would open a different page under this one's name.
public sealed class PageLinkRouter(Func<string?> profileServerUrl) {
    public string? PageIdFor(string url) {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var link)
            || !Uri.TryCreate(profileServerUrl(), UriKind.Absolute, out var server)
            || !string.Equals(link.Scheme, server.Scheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(link.Host, server.Host, StringComparison.OrdinalIgnoreCase)
            || link.Port != server.Port)
            return null;
        return PageReference.ParseId(url);
    }
}
