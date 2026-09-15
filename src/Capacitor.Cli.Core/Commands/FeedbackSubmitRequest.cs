using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Commands;

/// <summary>Request body for the tenant's <c>POST /api/feedback</c>. Built only through
/// <see cref="From"/>, the one place the enums become wire strings.</summary>
public sealed record FeedbackSubmitRequest(
        [property: JsonPropertyName("category")]          string                Category,
        [property: JsonPropertyName("message")]           string                Message,
        [property: JsonPropertyName("client_request_id")] Guid                  ClientRequestId,
        [property: JsonPropertyName("context")]           FeedbackSubmitContext Context
    ) {
    public static FeedbackSubmitRequest From(FeedbackSubmission submission) => new(
        Category:        Wire(submission.Category),
        Message:         submission.Message,
        ClientRequestId: submission.ClientRequestId,
        Context: new FeedbackSubmitContext(
            Source:        Wire(submission.Source),
            ClientVersion: CapacitorVersion.CurrentDisplay(),
            Os:            RuntimeInformation.OSDescription
        )
    );

    static string Wire(FeedbackCategory category) => category switch {
        FeedbackCategory.Bug      => "bug",
        FeedbackCategory.Feedback => "feedback",
        _                         => throw new ArgumentOutOfRangeException(nameof(category), category, null)
    };

    static string Wire(FeedbackSource source) => source switch {
        FeedbackSource.Cli     => "cli",
        FeedbackSource.Desktop => "desktop",
        _                      => throw new ArgumentOutOfRangeException(nameof(source), source, null)
    };
}
