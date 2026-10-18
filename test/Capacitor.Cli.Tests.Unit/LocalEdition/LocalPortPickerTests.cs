using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.LocalEdition;

namespace Capacitor.Cli.Tests.Unit.LocalEdition;

public class LocalPortPickerTests {
    static InstallRegistry Retiring(int port) =>
        new(null, [new RetiredInstallation("old", port, $"http://localhost:{port}")]);

    [Test]
    public async Task The_default_port_when_free() =>
        await Assert.That(LocalPortPicker.Choose(InstallRegistry.Empty, _ => true)).IsEqualTo(9473);

    [Test]
    public async Task A_busy_default_port_falls_back_to_a_free_non_retired_one() {
        var port = LocalPortPicker.Choose(InstallRegistry.Empty, p => p != 9473);

        await Assert.That(port).IsNotEqualTo(9473);
        await Assert.That(port).IsGreaterThan(0);
    }

    [Test]
    public async Task A_retired_default_is_skipped() =>
        await Assert.That(LocalPortPicker.Choose(Retiring(9473), _ => true)).IsNotEqualTo(9473);

    [Test]
    public async Task The_first_free_fallback_after_a_busy_default_is_taken() {
        var seen = new List<int>();
        var port = LocalPortPicker.Choose(InstallRegistry.Empty, p => {
            seen.Add(p);
            return seen.Count > 1;
        });

        await Assert.That(seen[0]).IsEqualTo(9473);
        await Assert.That(port).IsEqualTo(seen[1]);
    }

    [Test]
    public async Task Gives_up_after_twenty_attempts() {
        var calls = 0;

        await Assert.That(() => LocalPortPicker.Choose(InstallRegistry.Empty, _ => { calls++; return false; }))
            .Throws<InvalidOperationException>();
        await Assert.That(calls).IsEqualTo(21);
    }

    [Test]
    public async Task The_default_probe_accepts_a_port_nobody_holds() {
        var port = LocalPortPicker.Choose(InstallRegistry.Empty);

        await Assert.That(port).IsGreaterThan(0);
    }
}
