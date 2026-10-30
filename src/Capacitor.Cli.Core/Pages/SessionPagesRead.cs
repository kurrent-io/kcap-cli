using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.Plans;

namespace Capacitor.Cli.Core.Pages;

/// One read of the pages a session published, totalized the way the plans read is; Pages ride
/// only with Ready.
public sealed record SessionPagesRead(SessionPlansReadKind Kind, IReadOnlyList<ArtefactDto> Pages) {
    public static SessionPagesRead Of(SessionPlansReadKind kind) => new(kind, []);
}
