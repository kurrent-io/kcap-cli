using System.Security.Cryptography;
using System.Text;
using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class AgentFileNamesTests {
    [Test]
    [Arguments("../x")]
    [Arguments("a/b")]
    [Arguments("a\\b")]
    [Arguments("/etc/passwd")]
    [Arguments("agent-1")]
    public async Task Every_id_yields_a_fixed_length_lowercase_hex_name(string id) {
        var name = AgentFileNames.For(id);
        await Assert.That(name).Length().IsEqualTo(64);
        await Assert.That(name).Matches("^[0-9a-f]{64}$");
        await Assert.That(AgentFileNames.For(id)).IsEqualTo(name);
    }

    [Test]
    public async Task Name_is_the_sha256_the_pid_record_store_always_used() {
        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("agent-1"))).ToLowerInvariant();
        await Assert.That(AgentFileNames.For("agent-1")).IsEqualTo(expected);
        using var tmp = new TempDir();
        var store = new AgentPidRecordStore(tmp.Path, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        store.Write(new AgentPidRecord("agent-1", 1, "", PidIdentityKind.IdentityUnavailable, "agent", "pi", null, null, "d", "e", DateTimeOffset.UnixEpoch));
        await Assert.That(File.Exists(Path.Combine(tmp.Path, "agents", expected + ".json"))).IsTrue();
    }
}
