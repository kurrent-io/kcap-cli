namespace Capacitor.Cli.Core.Tests.Unit;

public class SessionIdTests {
    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("--")]
    [Arguments("..")]
    [Arguments("../escape")]
    public async Task A_value_that_names_no_file_of_its_own_is_refused(string? raw) =>
        await Assert.That(SessionId.Parse(raw)).IsNull();
}
