namespace Capacitor.Cli.Core.Http;

/// <summary>Whether a posted title came from the harness's own renamed-by-user store, or is the
/// harness's auto-generated title.</summary>
public enum HarnessTitleKind {
    Auto,
    Rename,
}
