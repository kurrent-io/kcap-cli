using Capacitor.Cli.Core.Policy;

namespace Capacitor.Cli.Core.Tests.Unit.Policy;

/// The server pins the same vectors: a hosted turn declared here must map to the id the server derives.
public class AcpItemTurnIdTests {
    [Test]
    [Arguments("019a1b2c-3d4e-7f60-8a9b-0c1d2e3f4a5b", "cdf29b8e-48e1-579f-812b-418c0ad3946c")]
    [Arguments("msg_0001", "482e2f44-ccce-56cf-b501-bfde23acce5e")]
    [Arguments("item-7", "a2b59e34-82e7-5308-aca2-47281aef3620")]
    [Arguments("", "bac75446-4272-5f51-b198-0275244f7642")]
    [Arguments("ü-非", "4ee5547f-438e-5f29-a291-8fd5d0146854")]
    public async Task Matches_the_shared_vectors(string itemId, string expected) =>
        await Assert.That(AcpItemTurnId.For(itemId)).IsEqualTo(Guid.Parse(expected));
}
