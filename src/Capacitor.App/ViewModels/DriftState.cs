namespace Capacitor.App.ViewModels;

/// How the working copy at a declared document's path compares with the declared snapshot.
/// Unknown when there is no root to look under, or the document was not declared.
public enum DriftState { Unknown, Same, Changed, Missing }
