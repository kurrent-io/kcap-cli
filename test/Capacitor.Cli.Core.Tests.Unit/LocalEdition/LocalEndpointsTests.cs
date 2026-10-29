using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Cli.Core.Tests.Unit.LocalEdition;

public class LocalEndpointsTests {
    static readonly LocalInstallation Installation = new("inst-1", 51234, "http://localhost:51234", "1.4.0");
    static readonly RetiredInstallation Purged      = new("inst-0", 50000, "http://localhost:50000");

    static readonly LocalEndpoints Installed =
        new(new StateRead<InstallRegistry>.Present(new InstallRegistry(Installation, [Purged])));

    static readonly LocalEndpoints Unreadable =
        new(new StateRead<InstallRegistry>.Unreadable("/x/install.json", "truncated"));

    [Test]
    [Arguments("http://localhost:51234")]
    [Arguments("http://127.0.0.1:51234")]
    [Arguments("http://[::1]:51234")]
    [Arguments("http://LOCALHOST:51234/")]
    public async Task Every_loopback_spelling_of_the_installation_is_local(string url) {
        await Assert.That(Installed.Classify(url)).IsEqualTo(new LocalEndpointClass.Local(Installation));
    }

    [Test]
    [Arguments("http://127.0.0.1:51234", "http://localhost:51234")]
    [Arguments("http://[::1]:51234/", "http://localhost:51234")]
    [Arguments("http://127.0.0.1:51234/base", "http://localhost:51234/base")]
    public async Task The_installation_is_rewritten_to_its_canonical_url(string url, string expected) {
        await Assert.That(Installed.Canonicalize(url)).IsEqualTo(expected);
    }

    [Test]
    [Arguments("https://acme.example")]
    [Arguments("http://localhost:6000")]
    [Arguments("https://localhost:51234")]
    [Arguments("http://127.0.0.2:51234")]
    [Arguments("http://127.0.0.1:51234?tenant=a")]
    [Arguments("http://127.0.0.1:51234#top")]
    [Arguments("not a url")]
    public async Task Anything_else_is_ordinary_and_left_as_written(string url) {
        await Assert.That(Installed.Classify(url)).IsTypeOf<LocalEndpointClass.Ordinary>();
        await Assert.That(Installed.Canonicalize(url)).IsEqualTo(url);
    }

    [Test]
    public async Task A_purged_installations_origin_is_retired() {
        await Assert.That(Installed.Classify("http://127.0.0.1:50000")).IsEqualTo(new LocalEndpointClass.Retired(Purged));
    }

    [Test]
    public async Task Without_a_registry_every_url_is_ordinary() {
        await Assert.That(LocalEndpoints.None.Classify("http://localhost:51234")).IsTypeOf<LocalEndpointClass.Ordinary>();
        await Assert.That(LocalEndpoints.None.Active).IsNull();
    }

    /// <summary>An unreadable registry is no evidence that a loopback URL is an ordinary server.</summary>
    [Test]
    public async Task An_unreadable_registry_leaves_loopback_undetermined() {
        await Assert.That(Unreadable.Classify("http://localhost:51234")).IsTypeOf<LocalEndpointClass.Undetermined>();
        await Assert.That(Unreadable.Classify("https://acme.example")).IsTypeOf<LocalEndpointClass.Ordinary>();
        await Assert.That(Unreadable.Canonicalize("http://127.0.0.1:51234")).IsEqualTo("http://127.0.0.1:51234");
    }
}
