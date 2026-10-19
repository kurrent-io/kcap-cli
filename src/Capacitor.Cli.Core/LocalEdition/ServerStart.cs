namespace Capacitor.Cli.Core.LocalEdition;

/// <summary>
/// What the server does at bootstrap while a journal entry stands. It reads only this and never
/// interprets the entry's phase. The zero value is <see cref="Deny"/> so an unset field fails closed.
/// </summary>
public enum ServerStart {
    /// <summary>The server refuses to start.</summary>
    Deny,

    /// <summary>The server starts normally.</summary>
    Allow,

    /// <summary>The server starts fenced.</summary>
    Fenced,
}
