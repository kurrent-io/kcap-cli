using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.Plans;

namespace Capacitor.Cli.Core.Pages;

/// One read of the pages citing a session, totalized the way the plans reads are; Pages is empty
/// unless Ready. Unavailable cannot tell a session this viewer may not see from a server without the
/// route: both answer 404.
public sealed record SessionPagesRead(SessionPlansReadKind Kind, IReadOnlyList<ArtefactDto> Pages) {
    public static SessionPagesRead Of(SessionPlansReadKind kind) => new(kind, []);
}
