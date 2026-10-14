using Capacitor.Cli.Core;
using Capacitor.Cli.Harness.MuseAssistant;

namespace Capacitor.Cli.Tests.Unit.Harness.MuseAssistant;

/// <summary>Both directory names are opaque hashes: a chat id or user id is never a path segment, two
/// accounts or two tenants never share a file, and ids differing only by case or by a slash stay apart.</summary>
public class MuseAssistantPathsTests {
    static readonly ConfigRoot Config = new("/cfg");
    static readonly MuseAssistantPrincipal Acme = new("https://acme.kcap.ai", "user_01HXYZ");

    [Test]
    public async Task Scope_dir_hashes_server_and_user_under_the_config_root() {
        await Assert.That(MuseAssistantPaths.ScopeDir(Config, Acme))
            .IsEqualTo(Path.Join("/cfg", "muse-assistant", "2cd7918f069b2edb1effab0afe9dde2c8c901b0fea1154882065db8c177e5247"));
    }

    [Test]
    public async Task Another_tenant_or_account_gets_another_directory() {
        var dir = MuseAssistantPaths.ScopeDir(Config, Acme);

        await Assert.That(MuseAssistantPaths.ScopeDir(Config, Acme with { ServerUrl = "https://other.kcap.ai" })).IsNotEqualTo(dir);
        await Assert.That(MuseAssistantPaths.ScopeDir(Config, Acme with { UserId = "user_01OTHER" })).IsNotEqualTo(dir);
    }

    [Test]
    public async Task Chat_key_is_the_lowercase_sha256_of_the_chat_id() {
        await Assert.That(MuseAssistantPaths.ChatKey("chat-main")).IsEqualTo("94f25dc9eb23f1f0086366f0a1b817e030f89bdcece9ad987fcbf5d65a7dfa86");
    }

    [Test]
    [Arguments("a/b")]
    [Arguments("../escape")]
    [Arguments("A/B")]
    [Arguments("..")]
    public async Task Awkward_chat_ids_stay_inside_the_directory(string chatId) {
        var dir   = "/cfg/muse-assistant/x";
        var state = MuseAssistantPaths.ChatState(dir, chatId);

        await Assert.That(Path.GetDirectoryName(state)).IsEqualTo(Path.GetDirectoryName(Path.Join(dir, "x")));
        await Assert.That(Path.GetFileName(state)).Matches("^[0-9a-f]{64}\\.json$");
        await Assert.That(MuseAssistantPaths.ChatLock(dir, chatId)).IsEqualTo(Path.Join(dir, MuseAssistantPaths.ChatKey(chatId) + ".lock"));
    }

    [Test]
    public async Task Chat_ids_differing_only_by_case_get_distinct_files() {
        await Assert.That(MuseAssistantPaths.ChatKey("a/b")).IsNotEqualTo(MuseAssistantPaths.ChatKey("A/B"));
    }
}
