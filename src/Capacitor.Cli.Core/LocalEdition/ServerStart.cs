namespace Capacitor.Cli.Core.LocalEdition;

/// <summary>
/// What the server does at bootstrap while a journal entry stands. It reads only this and never
/// interprets the entry's phase.
/// </summary>
public enum ServerStart {
    /// <summary>The server starts normally.</summary>
    Allow,

    /// <summary>The server starts fenced.</summary>
    Fenced,

    /// <summary>The server refuses to start.</summary>
    Deny,
}
