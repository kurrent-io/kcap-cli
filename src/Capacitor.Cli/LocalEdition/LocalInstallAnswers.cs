namespace Capacitor.Cli.LocalEdition;

/// <summary>What the user agreed to. Both are required for a new installation and ignored for a reuse.</summary>
/// <param name="SingleUser">The user accepted that the local edition serves only them.</param>
/// <param name="Confirmed">The user confirmed the install.</param>
sealed record LocalInstallAnswers(bool SingleUser, bool Confirmed);
