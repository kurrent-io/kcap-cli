using Capacitor.Cli.Harness.MuseAssistant;

namespace Capacitor.Cli.Tests.Unit.Harness.MuseAssistant;

/// <summary>State is bound to the directory it sits in: a file for another tenant, account or chat is
/// refused, not read; a missing scope file reads as empty; writes land whole or not at all.</summary>
public class MuseAssistantStateStoreTests {
    [TempDir] public required TempDir Tmp { get; init; }

    static readonly MuseAssistantPrincipal Acme  = new("https://acme.kcap.ai", "user_01HXYZ");
    static readonly MuseAssistantPrincipal Other = new("https://acme.kcap.ai", "user_01OTHER");

    static string StateFileName(string chatId) => MuseAssistantPaths.ChatKey(chatId) + ".json";

    static MuseAssistantChatState State(MuseAssistantPrincipal p, string chat = "chat-main", MuseAssistantOpenSession? open = null) =>
        new(chat, p.ServerUrl, p.UserId, new("0123456789abcdef0123456789abcdef", MuseAssistantGrantBoundaries.AfterSeq, 11, 1_760_000_000_000, null), 15, 1_760_000_012_000, open);

    [Test]
    public async Task State_round_trips_with_and_without_an_open_session() {
        var store = new MuseAssistantStateStore(Tmp.PathTo("scope"), Acme);
        var open  = new MuseAssistantOpenSession("451e8f8bd31baf5cf44a88b1ff4231d4", 12, 1_760_000_005_000, true, "/home/hatch", MuseAssistantSessionPhases.Started, null);

        store.SaveState(State(Acme, open: open));
        store.SaveState(State(Acme, chat: "chat-two"));

        await Assert.That(store.LoadState("chat-main").State).IsEqualTo(State(Acme, open: open));
        await Assert.That(store.LoadState("chat-two").State).IsEqualTo(State(Acme, chat: "chat-two"));
        await Assert.That(store.LoadState("chat-three")).IsEqualTo(((MuseAssistantChatState?)null, (string?)null));
        await Assert.That(File.Exists(MuseAssistantPaths.ChatState(Tmp.PathTo("scope"), "chat-main"))).IsTrue();
        await Assert.That(Directory.EnumerateFiles(Tmp.PathTo("scope"), "*.tmp")).IsEmpty();
    }

    [Test]
    public async Task A_write_that_cannot_replace_its_target_fails_without_leaving_a_temp_file() {
        var dir   = Tmp.CreateDir("scope");
        var store = new MuseAssistantStateStore(dir, Acme);
        dir.CreateDir(StateFileName("chat-main"));

        Exception? thrown = null;
        try { store.SaveState(State(Acme)); } catch (Exception e) { thrown = e; }

        await Assert.That(thrown is IOException or UnauthorizedAccessException).IsTrue();
        await Assert.That(Directory.EnumerateFiles(dir, "*.tmp")).IsEmpty();
    }

    [Test]
    public async Task A_write_waits_out_a_reader_holding_the_target_open() {
        var dir   = Tmp.CreateDir("scope");
        var store = new MuseAssistantStateStore(dir, Acme);
        store.SaveState(State(Acme));
        var updated = State(Acme) with { Watermark = 99 };

        Task saving;
        using (new FileStream(MuseAssistantPaths.ChatState(dir, "chat-main"), FileMode.Open, FileAccess.Read, FileShare.Read)) {
            saving = Task.Run(() => store.SaveState(updated));
            await Task.Delay(50);
        }

        await saving.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(store.LoadState("chat-main").State).IsEqualTo(updated);
        await Assert.That(Directory.EnumerateFiles(dir, "*.tmp")).IsEmpty();
    }

    [Test]
    public async Task A_state_file_copied_from_another_profile_is_refused() {
        var theirs = new MuseAssistantStateStore(Tmp.PathTo("theirs"), Other);
        theirs.SaveState(State(Other));
        var mine = new MuseAssistantStateStore(Tmp.PathTo("mine"), Acme);
        File.Copy(MuseAssistantPaths.ChatState(Tmp.PathTo("theirs"), "chat-main"), MuseAssistantPaths.ChatState(Tmp.CreateDir("mine"), "chat-main"));

        var (state, problem) = mine.LoadState("chat-main");

        await Assert.That(state).IsNull();
        await Assert.That(problem).Contains("another");
        await Assert.That(mine.EnumerateStates()).IsEmpty();
        await Assert.That(() => mine.SaveState(State(Other))).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Corrupt_state_is_a_problem_and_corrupt_or_missing_scope_reads_as_empty() {
        var dir   = Tmp.CreateDir("scope");
        var store = new MuseAssistantStateStore(dir, Acme);
        dir.CreateFile(StateFileName("chat-main"), "{not json");
        dir.CreateFile("scope.json", "{not json");

        await Assert.That(store.LoadState("chat-main").Problem).Contains("unreadable");
        await Assert.That(store.LoadScope().Chats).IsEmpty();
        await Assert.That(store.LoadScope().Roles).IsEquivalentTo(["user", "assistant", "commentary"]);
        await Assert.That(new MuseAssistantStateStore(Tmp.PathTo("absent"), Acme).LoadScope().Chats).IsEmpty();
    }

    [Test]
    public async Task Scope_round_trips_and_is_stamped_with_the_principal() {
        var store = new MuseAssistantStateStore(Tmp.PathTo("scope"), Acme);

        store.SaveScope(new("ignored", "ignored", MuseAssistantRoles.Selected, ["chat-main", "chat-two"]));

        var scope = store.LoadScope();
        await Assert.That(scope.ServerUrl).IsEqualTo(Acme.ServerUrl);
        await Assert.That(scope.UserId).IsEqualTo(Acme.UserId);
        await Assert.That(scope.Chats).IsEquivalentTo(["chat-main", "chat-two"]);
    }

    [Test]
    public async Task Enumerate_skips_scope_json_foreign_files_and_misnamed_copies() {
        var dir   = Tmp.CreateDir("scope");
        var store = new MuseAssistantStateStore(dir, Acme);
        store.SaveState(State(Acme));
        store.SaveScope(new(Acme.ServerUrl, Acme.UserId, MuseAssistantRoles.Selected, ["chat-main"]));
        File.Copy(MuseAssistantPaths.ChatState(dir, "chat-main"), dir.PathTo("renamed-copy.json"));

        await Assert.That(store.EnumerateStates().Select(s => s.ChatId)).IsEquivalentTo(["chat-main"]);
    }

    [Test]
    public async Task Roles_selected_are_user_assistant_and_commentary_only() {
        await Assert.That(MuseAssistantRoles.IsSelected("user")).IsTrue();
        await Assert.That(MuseAssistantRoles.IsSelected("assistant")).IsTrue();
        await Assert.That(MuseAssistantRoles.IsSelected("commentary")).IsTrue();
        await Assert.That(MuseAssistantRoles.IsSelected("developer")).IsFalse();
    }

    [Test]
    public async Task Grant_floor_is_the_seq_only_for_an_after_seq_boundary() {
        var afterSeq = new MuseAssistantGrant("g", MuseAssistantGrantBoundaries.AfterSeq, 11, 1, null);

        await Assert.That(afterSeq.Floor).IsEqualTo(11);
        await Assert.That(afterSeq.Revoked).IsFalse();
        await Assert.That((afterSeq with { Boundary = MuseAssistantGrantBoundaries.EmptyChat }).Floor).IsNull();
        await Assert.That((afterSeq with { Boundary = MuseAssistantGrantBoundaries.History }).Floor).IsNull();
        await Assert.That((afterSeq with { RevokedAtMs = 2 }).Revoked).IsTrue();
    }

    [Test]
    public async Task A_sparse_manifest_still_reads_with_nulls_for_what_is_missing() {
        var empty  = System.Text.Json.JsonSerializer.Deserialize("{}", MuseAssistantJsonContext.Default.MuseAssistantManifest);
        var sparse = System.Text.Json.JsonSerializer.Deserialize("""{"chat_id":"c","pages":[{"file":"p1.json"}]}""", MuseAssistantJsonContext.Default.MuseAssistantManifest);

        await Assert.That(empty).IsEqualTo(new MuseAssistantManifest(null, null, null, null));
        await Assert.That(sparse!.ChatId).IsEqualTo("c");
        await Assert.That(sparse.Pages).IsEquivalentTo([new MuseAssistantManifestPage("p1.json", null)]);
    }

    /// <summary>A grant scope add could never have written selects by accident: an unknown boundary or a
    /// missing after_seq has no floor, so it would read as the whole chat. It must be unusable instead.</summary>
    [Test]
    [Arguments("""null""")]
    [Arguments("""{"id":"g","boundary":"everything","after_seq":null,"granted_at_ms":1,"revoked_at_ms":null}""")]
    [Arguments("""{"id":"g","boundary":"after_seq","after_seq":null,"granted_at_ms":1,"revoked_at_ms":null}""")]
    [Arguments("""{"id":"g","boundary":"after_seq","after_seq":-1,"granted_at_ms":1,"revoked_at_ms":null}""")]
    [Arguments("""{"id":"g","boundary":"history","after_seq":5,"granted_at_ms":1,"revoked_at_ms":null}""")]
    [Arguments("""{"id":"g","boundary":"empty_chat","after_seq":0,"granted_at_ms":1,"revoked_at_ms":null}""")]
    [Arguments("""{"id":"","boundary":"history","after_seq":null,"granted_at_ms":1,"revoked_at_ms":null}""")]
    [Arguments("""{"id":null,"boundary":"history","after_seq":null,"granted_at_ms":1,"revoked_at_ms":null}""")]
    [Arguments("""{"id":"g","boundary":null,"after_seq":null,"granted_at_ms":1,"revoked_at_ms":null}""")]
    public async Task A_state_whose_grant_scope_add_could_not_have_written_is_unusable(string grant) {
        var dir   = Tmp.CreateDir("scope");
        var store = new MuseAssistantStateStore(dir, Acme);
        dir.CreateFile(StateFileName("chat-main"), $$"""{"chat_id":"chat-main","server_url":"{{Acme.ServerUrl}}","user_id":"{{Acme.UserId}}","grant":{{grant}},"watermark":null,"last_selected_at_ms":null,"open":null}""");

        var (state, problem) = store.LoadState("chat-main");

        await Assert.That(state).IsNull();
        await Assert.That(problem).IsNotNull();
        await Assert.That(store.EnumerateStates()).IsEmpty();
        await Assert.That(store.EnumerateUnusableStateFiles().Select(f => Path.GetFileName(f))).IsEquivalentTo([StateFileName("chat-main")]);
    }

    [Test]
    [Arguments("""{"id":"g","boundary":"after_seq","after_seq":0,"granted_at_ms":1,"revoked_at_ms":null}""")]
    [Arguments("""{"id":"g","boundary":"empty_chat","after_seq":null,"granted_at_ms":1,"revoked_at_ms":2}""")]
    [Arguments("""{"id":"g","boundary":"history","after_seq":null,"granted_at_ms":1,"revoked_at_ms":null}""")]
    public async Task A_state_with_a_grant_scope_add_writes_is_usable(string grant) {
        var dir   = Tmp.CreateDir("scope");
        var store = new MuseAssistantStateStore(dir, Acme);
        dir.CreateFile(StateFileName("chat-main"), $$"""{"chat_id":"chat-main","server_url":"{{Acme.ServerUrl}}","user_id":"{{Acme.UserId}}","grant":{{grant}},"watermark":null,"last_selected_at_ms":null,"open":null}""");

        await Assert.That(store.LoadState("chat-main").Problem).IsNull();
        await Assert.That(store.EnumerateStates().Select(s => s.ChatId)).IsEquivalentTo(["chat-main"]);
        await Assert.That(store.EnumerateUnusableStateFiles()).IsEmpty();
    }

    [Test]
    public async Task A_state_file_missing_a_required_field_is_unreadable_and_skipped() {
        var dir   = Tmp.CreateDir("scope");
        var store = new MuseAssistantStateStore(dir, Acme);
        var noGrant = $$"""{"chat_id":"chat-main","server_url":"{{Acme.ServerUrl}}","user_id":"{{Acme.UserId}}","watermark":null,"last_selected_at_ms":null,"open":null}""";
        var noChat  = $$"""{"server_url":"{{Acme.ServerUrl}}","user_id":"{{Acme.UserId}}","grant":{"id":"g","boundary":"history","after_seq":null,"granted_at_ms":1,"revoked_at_ms":null},"watermark":null,"last_selected_at_ms":null,"open":null}""";
        dir.CreateFile(StateFileName("chat-main"), noGrant);
        dir.CreateFile("no-chat.json", noChat);
        dir.CreateFile("scope.json", $$"""{"server_url":"{{Acme.ServerUrl}}","user_id":"{{Acme.UserId}}","chats":["chat-main"]}""");

        await Assert.That(store.LoadState("chat-main").Problem).Contains("unreadable");
        await Assert.That(store.EnumerateStates()).IsEmpty();
        await Assert.That(store.LoadScope().Chats).IsEmpty();
    }
}
