using Capacitor.Cli.Harness.MuseAssistant;

namespace Capacitor.Cli.Tests.Unit.Harness.MuseAssistant;

/// <summary>The session id is persisted as the session's identity on the server, so its derivation is
/// frozen here. It must also pass the server's dash-free shape check.</summary>
public class MuseAssistantSessionIdTests {
    [Test]
    public async Task Id_is_the_first_sixteen_bytes_of_the_sha256_in_lowercase_hex() {
        await Assert.That(MuseAssistantSessionId.For("user_01HXYZ", "chat-main", "0123456789abcdef0123456789abcdef", 12))
            .IsEqualTo("451e8f8bd31baf5cf44a88b1ff4231d4");
    }

    [Test]
    public async Task Grant_user_chat_and_first_seq_each_change_the_id() {
        var id = MuseAssistantSessionId.For("user_01HXYZ", "chat-main", "0123456789abcdef0123456789abcdef", 12);

        await Assert.That(MuseAssistantSessionId.For("user_01HXYZ", "chat-main", "ffffffffffffffffffffffffffffffff", 12)).IsEqualTo("ebaa4851e6ba4ec12687b220b52cd4b1");
        await Assert.That(MuseAssistantSessionId.For("user_01OTHER", "chat-main", "0123456789abcdef0123456789abcdef", 12)).IsEqualTo("128d86e2c7b511b6f36c48fa16b40c55");
        await Assert.That(MuseAssistantSessionId.For("user_01HXYZ", "chat-other", "0123456789abcdef0123456789abcdef", 12)).IsNotEqualTo(id);
        await Assert.That(MuseAssistantSessionId.For("user_01HXYZ", "chat-main", "0123456789abcdef0123456789abcdef", 13)).IsNotEqualTo(id);
        await Assert.That(id).Matches("^[0-9a-f]{32}$");
    }
}
