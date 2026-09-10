using Capacitor.Cli.Daemon.Services;
using TUnit.Assertions.Enums;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// The advertised capability list, pinned whole. Nothing may appear here that
/// <see cref="LocalControlServer"/> does not route to a live handler, so a new entry landing without
/// its routing case fails here rather than reaching a client that then asks for behavior the daemon
/// has not got.
/// </summary>
public class LocalControlCapabilitiesTests {
    [Test]
    public async Task The_advertised_list_is_exactly_the_routed_capabilities() =>
        await Assert.That(LocalControlCapabilities.Current)
            .IsEquivalentTo(new[] { "consent/1", "consent/2", "consent/3", "status/1", "permission/1", "input/1" },
                CollectionOrdering.Matching);
}
