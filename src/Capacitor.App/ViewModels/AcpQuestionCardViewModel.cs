using Capacitor.App.Services;

namespace Capacitor.App.ViewModels;

/// The NEEDS YOU card for an ACP elicitation: options are identified by id rather than label, and
/// the answer rides the server lane, which is the only one that can carry it.
public sealed class AcpQuestionCardViewModel : PendingCardViewModel {
    internal AcpElicitation Question { get; }
    internal IPermissionService Permissions { get; }

    public AcpQuestionCardViewModel(PendingPermissionRequest entry, IPermissionService permissions) : base(entry) {
        Question = entry.AcpQuestion ?? throw new ArgumentException("not an ACP question", nameof(entry));
        Permissions = permissions;
    }
}
