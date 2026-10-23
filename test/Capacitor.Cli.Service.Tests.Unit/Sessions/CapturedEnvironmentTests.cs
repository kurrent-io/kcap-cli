using Capacitor.Cli.Service.Sessions;

namespace Capacitor.Cli.Service.Tests.Unit.Sessions;

public class CapturedEnvironmentTests {
    [Test]
    public async Task A_json_object_of_strings_is_the_environment() {
        var env = CapturedEnvironment.Parse("""{"PATH":"C:\\nvm;C:\\tools","KCAP_URL":"https://kcap.example"}""");

        await Assert.That(env["path"]).IsEqualTo(@"C:\nvm;C:\tools");
        await Assert.That(env["KCAP_URL"]).IsEqualTo("https://kcap.example");
    }

    [Test]
    [Arguments("not json")]
    [Arguments("[1,2]")]
    [Arguments("""{"A":1}""")]
    [Arguments("null")]
    public async Task Anything_else_is_empty(string text) =>
        await Assert.That(CapturedEnvironment.Parse(text)).IsEmpty();

    [Test]
    public async Task Names_that_cannot_be_variables_are_dropped() {
        var env = CapturedEnvironment.Parse("{\"A=B\":\"x\",\"\":\"y\",\"OK\":\"z\",\"NUL\":\"a\\u0000b\"}");

        await Assert.That(env.Keys).IsEquivalentTo(["OK"]);
    }
}
