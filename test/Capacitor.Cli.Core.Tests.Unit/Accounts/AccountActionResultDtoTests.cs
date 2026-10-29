using System.Text.Json;
using Capacitor.Cli.Core.Accounts;

namespace Capacitor.Cli.Core.Tests.Unit.Accounts;

public class AccountActionResultDtoTests {
    [Test]
    public async Task Result_serializes_on_one_line_in_snake_case_with_every_field_present() {
        var result = new AccountActionResultDto("remove", null, false, [new WiringStepDto("hooks", false, "could not write the settings file")], null, "It stays registered.");

        var json = JsonSerializer.Serialize(result, AccountActionResultJsonContext.Default.AccountActionResultDto);

        await Assert.That(json).IsEqualTo(
            """{"action":"remove","account_id":null,"succeeded":false,"steps":[{"name":"hooks","succeeded":false,"detail":"could not write the settings file"}],"revision":null,"error":"It stays registered."}""");
    }
}
