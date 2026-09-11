using Capacitor.Cli.Core.WorkItems;

namespace Capacitor.Cli.Core.Tests.Unit.WorkItems;

public class WorkContextIdsTests {
    [Test]
    public async Task Guid_session_id_reaches_the_route_in_n_form() =>
        await Assert.That(WorkContextIds.CanonicalSessionId("8bc7255f-2453-4efd-a733-0af4b6ae9f20")).IsEqualTo("8bc7255f24534efda7330af4b6ae9f20");

    [Test]
    public async Task Opaque_dashed_id_reaches_the_route_unchanged() =>
        await Assert.That(WorkContextIds.CanonicalSessionId("sess-1")).IsEqualTo("sess-1");

    [Test]
    public async Task Dot_segments_and_blanks_are_still_rejected() {
        await Assert.That(WorkContextIds.CanonicalSessionId(".")).IsNull();
        await Assert.That(WorkContextIds.CanonicalSessionId("..")).IsNull();
        await Assert.That(WorkContextIds.CanonicalSessionId(" ")).IsNull();
    }
}
