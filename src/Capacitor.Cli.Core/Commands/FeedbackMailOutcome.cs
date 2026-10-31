namespace Capacitor.Cli.Core.Commands;

/// <param name="Opened">A mail handler was launched; not proof a draft appeared.</param>
public sealed record FeedbackMailOutcome(FeedbackMailDraft Draft, bool Opened, bool Copied);
