namespace Capacitor.Cli.Daemon.Harness.Muse;

/// <summary>One answer in <c>userInput/answer</c>; exactly one of the three value members is set.</summary>
internal sealed record MuseUserInputAnswer(
    string                QuestionId,
    string?               SelectedLabel  = null,
    IReadOnlyList<string>? SelectedLabels = null,
    string?               FreeText       = null
);
