namespace Capacitor.App.ViewModels;

/// The one state a pull request is summarised to for a colour: the rail's branch glyph and the
/// card's lifecycle label read the same value. Ordered by how much the state needs the user.
public enum PullRequestTone { None, Closed, Merged, Draft, Ready, ChecksRunning, Conflict, ChecksFailed }
