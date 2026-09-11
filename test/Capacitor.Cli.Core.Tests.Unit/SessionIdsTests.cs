namespace Capacitor.Cli.Core.Tests.Unit;

public class SessionIdsTests {
    [Test]
    public async Task Dashed_guid_becomes_its_n_form() =>
        await Assert.That(SessionIds.Canonical("8BC7255F-2453-4EFD-A733-0AF4B6AE9F20")).IsEqualTo("8bc7255f24534efda7330af4b6ae9f20");

    [Test]
    public async Task Opaque_id_is_returned_trimmed_and_unchanged() =>
        await Assert.That(SessionIds.Canonical("  sess-1 ")).IsEqualTo("sess-1");

    [Test]
    public async Task Null_and_blank_are_null() {
        await Assert.That(SessionIds.Canonical(null)).IsNull();
        await Assert.That(SessionIds.Canonical("   ")).IsNull();
    }
}
