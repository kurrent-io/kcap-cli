using Capacitor.Cli.Daemon.Services;
using TUnit.Assertions.Enums;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// The advertised capability list, pinned whole, so a new entry lands only with a deliberate edit here:
/// each must be backed by a routed handler or by status fields the daemon fills, never reach a client
/// that then asks for behavior the daemon has not got.
/// </summary>
public class LocalControlCapabilitiesTests {
    [Test]
    public async Task The_advertised_list_is_pinned_whole() =>
        await Assert.That(LocalControlCapabilities.Current)
            .IsEquivalentTo(new[] { "consent/1", "consent/2", "consent/3", "status/1", "permission/1", "input/1", "input/2", "settings/1", "fence/1", "shutdown/1", "resume/1", "attach-runtime/1", "agent-session/1", "accounts/1", "experimental/1", "rename/1", "handoff/1", "headless/1" },
                CollectionOrdering.Matching);
}
