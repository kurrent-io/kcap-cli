namespace Capacitor.Cli.Daemon.Harness.Muse;

/// <summary>The choice to send back for an <c>approval/requested</c>, with the feedback text when the choice takes any.</summary>
internal sealed record MuseApprovalDecision(string ChoiceId, string? Feedback);
