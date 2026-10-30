namespace Capacitor.Cli.Core.Pages;

/// Whether the viewer may see a page's results. Unknown is a read that could not decide; SignedOut
/// is a viewer with no credential, which asking again will not change.
public enum PageResultsAccess { Visible, Hidden, Unknown, SignedOut }
