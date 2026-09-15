namespace Capacitor.Cli.Core.Commands;

/// <summary>One report as a caller means it. <see cref="ClientRequestId"/> is the idempotency key the
/// server dedupes on, so a retry of the SAME report must reuse it — mint it once, thread it through.</summary>
public sealed record FeedbackSubmission(
    FeedbackCategory Category,
    string           Message,
    Guid             ClientRequestId,
    FeedbackSource   Source
);
