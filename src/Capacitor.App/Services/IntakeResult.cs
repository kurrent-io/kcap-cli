using Capacitor.App.ViewModels;

namespace Capacitor.App.Services;

public sealed record IntakeResult(IReadOnlyList<StagedAttachment> Accepted, IReadOnlyList<IntakeRefusal> Refused) {
    public static readonly IntakeResult Empty = new([], []);
}
