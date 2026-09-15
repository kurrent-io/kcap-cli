using Capacitor.Cli.Core.Commands;

namespace Capacitor.Cli.Core.Tests.Unit;

public class FeedbackSubmitRequestTests {
    static FeedbackSubmission Submission(FeedbackCategory category, FeedbackSource source, Guid? id = null) =>
        new(category, "It broke.", id ?? Guid.NewGuid(), source);

    [Test]
    [Arguments(FeedbackCategory.Bug, "bug")]
    [Arguments(FeedbackCategory.Feedback, "feedback")]
    public async Task Category_serialises_to_its_lowercase_wire_value(FeedbackCategory category, string wire) {
        var request = FeedbackSubmitRequest.From(Submission(category, FeedbackSource.Cli));

        await Assert.That(request.Category).IsEqualTo(wire);
    }

    [Test]
    [Arguments(FeedbackSource.Cli, "cli")]
    [Arguments(FeedbackSource.Desktop, "desktop")]
    public async Task Source_serialises_to_its_lowercase_wire_value(FeedbackSource source, string wire) {
        var request = FeedbackSubmitRequest.From(Submission(FeedbackCategory.Bug, source));

        await Assert.That(request.Context.Source).IsEqualTo(wire);
    }

    [Test]
    public async Task Message_and_client_request_id_pass_through_unchanged() {
        var id      = Guid.NewGuid();
        var request = FeedbackSubmitRequest.From(Submission(FeedbackCategory.Feedback, FeedbackSource.Desktop, id));

        await Assert.That(request.Message).IsEqualTo("It broke.");
        await Assert.That(request.ClientRequestId).IsEqualTo(id);
        await Assert.That(request.Context.ClientVersion).IsEqualTo(CapacitorVersion.CurrentDisplay());
        await Assert.That(request.Context.Os).IsNotEmpty();
    }
}
