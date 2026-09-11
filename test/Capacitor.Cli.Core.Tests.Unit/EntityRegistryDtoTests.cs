using System.Text.Json;

namespace Capacitor.Cli.Core.Tests.Unit;

/// <summary>
/// Deserialization tests for the <c>kcap entities</c> DTOs against a sample payload mirroring the
/// server's <c>RepoEntityRegistryResponse</c>
/// (src/Capacitor.Api.Public.Abstractions/WorkItems/) — snake_case wire, so no client-side
/// [JsonPropertyName] typo can land without a test catching it.
/// </summary>
public class EntityRegistryDtoTests {
    [Test]
    public async Task CliEntityRegistry_DeserializesFromServerShape() {
        const string json = """
            {
              "curated": true,
              "registered": [
                { "value": "acme-prod", "kind": "tenant",   "source": "declared" },
                { "value": "kcap-server", "kind": "resource", "source": "repository" }
              ],
              "candidates": [
                { "value": "globex", "sessions": 6 }
              ]
            }
            """;

        var registry = JsonSerializer.Deserialize(json, CapacitorJsonContext.Default.CliEntityRegistry);

        await Assert.That(registry).IsNotNull();
        await Assert.That(registry!.Curated).IsTrue();
        await Assert.That(registry.Registered.Select(r => (r.Value, r.Kind, r.Source)))
            .IsEquivalentTo([("acme-prod", "tenant", "declared"), ("kcap-server", "resource", "repository")]);
        await Assert.That(registry.Candidates.Single().Sessions).IsEqualTo(6);
    }

    [Test]
    public async Task Requests_serialize_to_the_server_shape() {
        var register = JsonSerializer.Serialize(
            new CliRegisterEntityRequest { RepoHash = "abc", Value = "acme-prod", Kind = "tenant" },
            CapacitorJsonContext.Default.CliRegisterEntityRequest);

        await Assert.That(register).IsEqualTo("""{"repo_hash":"abc","value":"acme-prod","kind":"tenant"}""");

        var withdraw = JsonSerializer.Serialize(
            new CliWithdrawEntityRequest { RepoHash = "abc", Value = "acme-prod" },
            CapacitorJsonContext.Default.CliWithdrawEntityRequest);

        await Assert.That(withdraw).IsEqualTo("""{"repo_hash":"abc","value":"acme-prod"}""");
    }
}
