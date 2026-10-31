using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Core.Pages;

/// One read of a page's HTML at a pinned version.
public abstract record PageContentRead {
    PageContentRead() { }

    public sealed record Found(ArtefactDto Page, int Version, string Html) : PageContentRead;
    public sealed record NotFound : PageContentRead;
    public sealed record SignedOut : PageContentRead;
    public sealed record Unreachable : PageContentRead;
}
