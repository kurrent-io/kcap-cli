using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>A direct or brew install moves the CLI without the stack, so the passive notice names the coordinator.</summary>
public class UpdateNoticeStackBehindTests {
    static LocalEndpointClass Local(string version) => new LocalEndpointClass.Local(new("id", 47391, "http://localhost:47391", version));

    [Test]
    public async Task A_stack_behind_this_kcap_names_kcap_local_upgrade() =>
        await Assert.That(UpdateNotice.StackBehind(Local("1.2.0"), "1.3.0")).Contains("Run `kcap local upgrade`");

    [Test]
    [Arguments("1.3.0")]
    [Arguments("1.4.0")]
    public async Task A_stack_at_or_ahead_of_this_kcap_says_nothing(string stack) =>
        await Assert.That(UpdateNotice.StackBehind(Local(stack), "1.3.0")).IsNull();

    [Test]
    public async Task Another_server_says_nothing() =>
        await Assert.That(UpdateNotice.StackBehind(new LocalEndpointClass.Ordinary(), "1.3.0")).IsNull();
}
