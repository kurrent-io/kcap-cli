using Capacitor.Cli.Commands;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Capacitor.Cli.Tests.Unit;

public class UpdateChannelResolveTests {
    [Test]
    public async Task Defaults_to_latest() =>
        await Assert.That(UpdateCommand.ResolveChannel([], null)).IsEqualTo("latest");

    [Test]
    public async Task Config_beta_is_honoured() =>
        await Assert.That(UpdateCommand.ResolveChannel([], "beta")).IsEqualTo("beta");

    [Test]
    public async Task Beta_flag_overrides_config() =>
        await Assert.That(UpdateCommand.ResolveChannel(["--beta"], "latest")).IsEqualTo("beta");

    [Test]
    public async Task Stable_flag_overrides_config_beta() =>
        await Assert.That(UpdateCommand.ResolveChannel(["--stable"], "beta")).IsEqualTo("latest");
}
