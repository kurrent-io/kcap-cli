using Capacitor.Cli.Core.Auth;

namespace Capacitor.Cli.Core.Tests.Unit.Auth;

/// <summary>A base other than production is honoured only when the server and the base are both loopback.</summary>
public class WorkOSApiBaseRuleTests {
    const string Production = "https://api.workos.com";

    [Test]
    [Arguments("http://localhost:5108", "http://localhost:4100", "http://localhost:4100")]
    [Arguments("https://127.0.0.1:5109", "http://127.0.0.1:4100/", "http://127.0.0.1:4100")]
    [Arguments("http://[::1]:5108", "http://[::1]:4100", "http://[::1]:4100")]
    public async Task A_loopback_server_may_point_at_a_loopback_base(string server, string advertised, string expected) {
        await Assert.That(WorkOSApiBaseRule.Resolve(server, advertised, out var refused)).IsEqualTo(expected);
        await Assert.That(refused).IsFalse();
    }

    [Test]
    [Arguments("https://acme.kcap.ai", "http://localhost:4100")]
    [Arguments("http://localhost:5108", "https://workos.attacker.example")]
    [Arguments("http://localhost:5108", "http://user:pw@localhost:4100")]
    [Arguments("http://localhost:5108", "ftp://localhost:4100")]
    [Arguments("http://localhost:5108", "localhost:4100")]
    [Arguments(null, "http://localhost:4100")]
    public async Task Any_other_advertised_base_is_refused_for_production(string? server, string advertised) {
        await Assert.That(WorkOSApiBaseRule.Resolve(server, advertised, out var refused)).IsEqualTo(Production);
        await Assert.That(refused).IsTrue();
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("https://api.workos.com/")]
    public async Task No_base_or_production_is_production_without_a_refusal(string? advertised) {
        await Assert.That(WorkOSApiBaseRule.Resolve("https://acme.kcap.ai", advertised, out var refused)).IsEqualTo(Production);
        await Assert.That(refused).IsFalse();
    }

    [Test]
    public async Task Production_is_stamped_as_nothing() {
        await Assert.That(WorkOSApiBaseRule.Stamp(Production)).IsNull();
        await Assert.That(WorkOSApiBaseRule.Stamp("http://localhost:4100")).IsEqualTo("http://localhost:4100");
    }
}
