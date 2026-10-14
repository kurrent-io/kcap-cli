using Capacitor.Cli.Harness.MuseAssistant;
using Microsoft.Extensions.Time.Testing;
using static Capacitor.Cli.Tests.Unit.Harness.MuseAssistant.WalkBuilder;

namespace Capacitor.Cli.Tests.Unit.Harness.MuseAssistant;

/// <summary>Consent is a kcap-side grant: written in the user's turn, bounded in the chat's own order, kept
/// beside the watermark, revoked in the same write as the closure it implies, and reconciled by every
/// command so a crash between the two files cannot leave a chat half-granted.</summary>
public class MuseAssistantScopeTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }
    [TempHome]       public required TempHome       Home   { get; init; }
    [TempDir]        public required TempDir        Walks  { get; init; }

    const long Minute = 60_000, Hour = 3_600_000, T0 = 1_760_000_000_000;
    static readonly MuseAssistantPrincipal Acme = new("https://acme.kcap.ai", "user_01HXYZ");
    static readonly MuseAssistantBoundary History = new(MuseAssistantGrantBoundaries.History, null);
    static readonly MuseAssistantBoundary EmptyChat = new(MuseAssistantGrantBoundaries.EmptyChat, null);
    static MuseAssistantBoundary After(long seq) => new(MuseAssistantGrantBoundaries.AfterSeq, seq);

    readonly FakeTimeProvider _time = new(DateTimeOffset.FromUnixTimeMilliseconds(T0 + 4 * Hour));

    MuseAssistantStateStore Store(MuseAssistantPrincipal? p = null) => new(MuseAssistantPaths.ScopeDir(Config.Root, p ?? Acme), p ?? Acme);
    MuseAssistantChatState State(string chat = "chat-main") => Store().LoadState(chat).State!;

    // The fake clock freezes grant timestamps. A test that waits on a lock either passes the system clock
    // and lets the wait run in real time, or drives the fake clock to the timeout with TimedOut.
    async Task<MuseAssistantScope> ScopeAsync(RecordingPoster poster, string userId = "user_01HXYZ", TimeProvider? time = null, string serverUrl = "https://acme.kcap.ai") =>
        new(Config.Root, await MuseAssistantTestIdentity.SignedInAsync(Config.Root, serverUrl, userId), new(), new MuseAssistantActionRunner(poster, time ?? _time), time ?? _time);

    async Task<MuseAssistantIngest> IngestAsync(RecordingPoster poster, string userId = "user_01HXYZ", TimeProvider? time = null, string serverUrl = "https://acme.kcap.ai") =>
        new(Config.Root, Home.Home, await MuseAssistantTestIdentity.SignedInAsync(Config.Root, serverUrl, userId), new(), new MuseAssistantActionRunner(poster, time ?? _time), time ?? _time);

    // Drives a lock wait on the fake clock to its timeout, in bounded steps.
    async Task<int> TimedOut(Task<int> waiting) {
        for (var i = 0; !waiting.IsCompleted && i++ < 1000;) { _time.Advance(TimeSpan.FromSeconds(1)); await Task.Delay(1); }

        return await waiting.WaitAsync(TimeSpan.FromSeconds(10));
    }

    string Walk(string name, long fetchedAt, params WalkBuilder.Item[] items) => new WalkBuilder(Walks) { FetchedAtMs = fetchedAt }.AddPage(null, null, false, items).Write(name);

    [Test]
    public async Task Add_writes_a_grant_with_a_random_id_then_lists_the_chat_and_a_repeat_is_a_no_op() {
        var scope = await ScopeAsync(new RecordingPoster());

        await Assert.That(await scope.AddAsync("chat-main", After(41), default)).IsEqualTo(0);

        var state = State();
        await Assert.That(state.Grant.Id).Matches("^[0-9a-f]{32}$");
        await Assert.That(state.Grant.Boundary).IsEqualTo(MuseAssistantGrantBoundaries.AfterSeq);
        await Assert.That(state.Grant.AfterSeq).IsEqualTo(41);
        await Assert.That(state.Grant.GrantedAtMs).IsEqualTo(T0 + 4 * Hour);
        await Assert.That(state.Watermark).IsNull();
        await Assert.That(Store().LoadScope().Chats).IsEquivalentTo(["chat-main"]);

        await Assert.That(await scope.AddAsync("chat-main", After(41), default)).IsEqualTo(0);
        await Assert.That(State().Grant).IsEqualTo(state.Grant);                         // no new grant, so no new session id
    }

    [Test]
    [NotInParallel]
    [Arguments("history", null)]
    [Arguments("empty_chat", null)]
    [Arguments("after_seq", 30L)]
    [Arguments("after_seq", 100L)]
    [Arguments("after_seq", 45L)]
    public async Task Add_with_a_boundary_other_than_the_active_grants_exits_2_and_changes_nothing(string kind, long? seq) {
        var poster = new RecordingPoster();
        var scope  = await ScopeAsync(poster);
        await scope.AddAsync("chat-main", After(41), default);
        var granted   = State().Grant;
        var posted    = poster.Accepted.Count;
        var different = new MuseAssistantBoundary(kind, seq);
        using var output = ConsoleOutput.StartFullCapture("\n");

        await Assert.That(await scope.AddAsync("chat-main", different, default)).IsEqualTo(2);

        await Assert.That(State().Grant).IsEqualTo(granted);
        await Assert.That(poster.Accepted.Count).IsEqualTo(posted);
        await Assert.That(output.GetCapturedOutput()).IsEmpty();
        await Assert.That(output.GetCapturedError()).Contains("chat already recorded with").And.Contains("after_seq=41").And.Contains("scope remove --chat \"chat-main\"");

        var store = Store();                                                              // granted, but the list write was lost
        store.SaveScope(store.LoadScope() with { Chats = [] });
        await Assert.That(await scope.AddAsync("chat-main", different, default)).IsEqualTo(2);
        await Assert.That(State().Grant).IsEqualTo(granted);
    }

    [Test]
    [NotInParallel]
    [Arguments("history")]
    [Arguments("empty_chat")]
    public async Task Add_of_a_different_kind_than_an_active_non_seq_grant_exits_2(string kind) {
        var scope = await ScopeAsync(new RecordingPoster());
        await scope.AddAsync("chat-main", new MuseAssistantBoundary(kind, null), default);
        var granted = State().Grant;
        var other   = kind == "history" ? new MuseAssistantBoundary("empty_chat", null) : new MuseAssistantBoundary("history", null);
        using var output = ConsoleOutput.StartFullCapture("\n");

        await Assert.That(await scope.AddAsync("chat-main", other, default)).IsEqualTo(2);
        await Assert.That(await scope.AddAsync("chat-main", After(5), default)).IsEqualTo(2);

        await Assert.That(State().Grant).IsEqualTo(granted);
        await Assert.That(output.GetCapturedOutput()).IsEmpty();
        await Assert.That(await scope.AddAsync("chat-main", new MuseAssistantBoundary(kind, null), default)).IsEqualTo(0);
    }

    [Test]
    public async Task Two_grants_in_the_same_millisecond_and_after_a_clock_rollback_get_distinct_ids() {
        var scope = await ScopeAsync(new RecordingPoster());
        await scope.AddAsync("chat-main", History, default);
        var first = State().Grant.Id;
        await scope.RemoveAsync("chat-main", default);
        await scope.AddAsync("chat-main", History, default);
        var second = State().Grant.Id;
        // A later process on a clock set back: FakeTimeProvider cannot move backwards, so it gets its own.
        var rolledBack = await ScopeAsync(new RecordingPoster(), time: new FakeTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(T0)));
        await rolledBack.RemoveAsync("chat-main", default);
        await rolledBack.AddAsync("chat-main", History, default);

        await Assert.That(State().Grant.GrantedAtMs).IsEqualTo(T0);
        await Assert.That(new[] { first, second, State().Grant.Id }.Distinct().Count()).IsEqualTo(3);
    }

    [Test]
    public async Task Remove_ends_the_open_session_then_re_add_inside_the_gap_sends_only_items_above_the_new_boundary_to_a_new_session() {
        var poster = await Record(async scope => await scope.AddAsync("chat-main", History, default));
        await (await IngestAsync(poster)).RunAsync(Walk("w1", T0 + Minute, User(1, T0), Assistant(2, T0 + 1)), default);
        var firstSession = State().Open!.SessionId;

        var scope = await ScopeAsync(poster);
        await Assert.That(await scope.RemoveAsync("chat-main", default)).IsEqualTo(0);
        await Assert.That(poster.Ends).IsEquivalentTo([firstSession]);
        await Assert.That(State().Grant.Revoked).IsTrue();
        await Assert.That(State().Open).IsNull();
        await Assert.That(Store().LoadScope().Chats).IsEmpty();

        // Items 3 and 4 arrive while revoked; the user re-grants having just read seq 4.
        await Assert.That(await scope.AddAsync("chat-main", After(4), default)).IsEqualTo(0);
        await Assert.That(State().Watermark).IsEqualTo(2);
        await (await IngestAsync(poster)).RunAsync(Walk("w2", T0 + 10 * Minute, User(1, T0), Assistant(2, T0 + 1), User(3, T0 + 2 * Minute), User(4, T0 + 3 * Minute), User(5, T0 + 4 * Minute)), default);

        await Assert.That(poster.Seqs).IsEquivalentTo([1, 2, 5]);
        await Assert.That(poster.Starts.Distinct().Count()).IsEqualTo(2);
        await Assert.That(poster.Starts.Last()).IsNotEqualTo(firstSession);
        await Assert.That(State().Watermark).IsEqualTo(5);
    }

    [Test]
    public async Task Re_add_with_history_sends_nothing_below_the_watermark() {
        var poster = await Record(async scope => await scope.AddAsync("chat-main", History, default));
        await (await IngestAsync(poster)).RunAsync(Walk("w1", T0 + Minute, User(1, T0), Assistant(2, T0 + 1)), default);
        var scope = await ScopeAsync(poster);
        await scope.RemoveAsync("chat-main", default);

        await scope.AddAsync("chat-main", History, default);
        await (await IngestAsync(poster)).RunAsync(Walk("w2", T0 + 5 * Minute, User(1, T0), Assistant(2, T0 + 1), User(3, T0 + 2 * Minute)), default);

        await Assert.That(poster.Seqs).IsEquivalentTo([1, 2, 3]);
    }

    [Test]
    public async Task Remove_waits_for_a_running_ingest_and_the_run_posts_nothing_after_the_revocation_is_written() {
        var poster = await Record(async scope => await scope.AddAsync("chat-main", History, default));
        var dir    = MuseAssistantPaths.ScopeDir(Config.Root, Acme);

        Task<int> remove;
        using (MuseAssistantChatLock.TryAcquire(MuseAssistantPaths.ChatLock(dir, "chat-main"))) {
            remove = (await ScopeAsync(poster, time: TimeProvider.System)).RemoveAsync("chat-main", default);
            await Task.Delay(600);
            await Assert.That(remove.IsCompleted).IsFalse();
        }

        await Assert.That(await remove.WaitAsync(TimeSpan.FromSeconds(10))).IsEqualTo(0);
        await Assert.That(State().Grant.Revoked).IsTrue();

        var late = new RecordingPoster();
        await Assert.That(await (await IngestAsync(late)).RunAsync(Walk("late", T0 + Minute, User(1, T0)), default)).IsEqualTo(3);
        await Assert.That(late.Accepted).IsEmpty();
    }

    [Test]
    public async Task Remove_during_pending_start_replays_the_start_then_ends() {
        await (await ScopeAsync(new RecordingPoster())).AddAsync("chat-main", History, default);
        var state = State();
        var open  = new MuseAssistantOpenSession(MuseAssistantSessionId.For(Acme.UserId, "chat-main", state.Grant.Id, 1), 1, T0, true, Home.Path, MuseAssistantSessionPhases.PendingStart, null);
        Store().SaveState(state with { Open = open });
        var poster = new RecordingPoster();

        await Assert.That(await (await ScopeAsync(poster)).RemoveAsync("chat-main", default)).IsEqualTo(0);

        await Assert.That(poster.Accepted.Select(a => a.GetType().Name)).IsEquivalentTo([nameof(MuseAssistantStartSession), nameof(MuseAssistantEndSession)]);
        await Assert.That(((MuseAssistantEndSession)poster.Accepted[1]).EndedAtMs).IsEqualTo(T0);
    }

    [Test]
    public async Task Remove_with_a_failing_closure_keeps_revoked_and_pending_end_listed_as_closing_and_sweep_completes_it() {
        var poster = await Record(async scope => await scope.AddAsync("chat-main", History, default));
        await (await IngestAsync(poster)).RunAsync(Walk("w1", T0 + Minute, User(1, T0)), default);
        var failing = new RecordingPoster { Refuse = (_, _) => "hooks/session-end/muse-assistant answered 503" };

        await Assert.That(await (await ScopeAsync(failing)).RemoveAsync("chat-main", default)).IsEqualTo(1);
        await Assert.That(State().Grant.Revoked).IsTrue();
        await Assert.That(State().Open!.Phase).IsEqualTo(MuseAssistantSessionPhases.PendingEnd);
        await Assert.That(Store().LoadScope().Chats).IsEquivalentTo(["chat-main"]);

        var sweeping = new RecordingPoster();
        await Assert.That(await (await ScopeAsync(sweeping)).SweepAsync(default)).IsEqualTo(0);
        await Assert.That(sweeping.Ends.Count()).IsEqualTo(1);
        await Assert.That(State().Open).IsNull();
        await Assert.That(Store().LoadScope().Chats).IsEmpty();
    }

    [Test]
    public async Task Concurrent_adds_for_different_chats_and_a_reset_racing_an_add_leave_every_active_chat_listed() {
        var scope = await ScopeAsync(new RecordingPoster(), time: TimeProvider.System);

        await Task.WhenAll(scope.AddAsync("chat-a", History, default), scope.AddAsync("chat-b", History, default), scope.AddAsync("chat-c", History, default));
        await Assert.That(Store().LoadScope().Chats.Order()).IsEquivalentTo(["chat-a", "chat-b", "chat-c"]);

        await Task.WhenAll(scope.ResetAsync(default), scope.AddAsync("chat-d", History, default));
        var listed = Store().LoadScope().Chats;
        var active = Store().EnumerateStates().Where(s => !s.Grant.Revoked).Select(s => s.ChatId).ToList();
        await Assert.That(listed.Order()).IsEquivalentTo(active.Order());
    }

    [Test]
    public async Task A_crash_between_the_two_files_is_reconciled_by_the_next_add_or_sweep() {
        var scope = await ScopeAsync(new RecordingPoster());
        var store = Store();

        // State written, list not: the next add lists it without a new grant.
        store.SaveState(new("chat-a", Acme.ServerUrl, Acme.UserId, new("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", MuseAssistantGrantBoundaries.History, null, T0, null), null, null, null));
        await scope.AddAsync("chat-a", History, default);
        await Assert.That(State("chat-a").Grant.Id).IsEqualTo("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        await Assert.That(Store().LoadScope().Chats).Contains("chat-a");

        // Listed, no state: sweep reports it (exit 3) and replays nothing; add grants it.
        store.SaveScope(store.LoadScope() with { Chats = [.. store.LoadScope().Chats, "chat-b"] });
        var poster = new RecordingPoster();
        await Assert.That(await (await ScopeAsync(poster)).SweepAsync(default)).IsEqualTo(3);
        await Assert.That(poster.Accepted).IsEmpty();
        await Assert.That(Store().LoadState("chat-b").State).IsNull();
        await scope.AddAsync("chat-b", After(7), default);
        await Assert.That(State("chat-b").Grant.AfterSeq).IsEqualTo(7);

        // State says active, list dropped it: sweep relists.
        store.SaveScope(store.LoadScope() with { Chats = [] });
        await (await ScopeAsync(new RecordingPoster())).SweepAsync(default);
        await Assert.That(Store().LoadScope().Chats.Order()).IsEquivalentTo(["chat-a", "chat-b"]);
    }

    [Test]
    public async Task Another_accounts_pending_closure_is_completed_only_when_that_account_signs_back_in() {
        var a = await Record(async scope => await scope.AddAsync("chat-main", History, default));
        await (await IngestAsync(a)).RunAsync(Walk("a1", T0 + Minute, User(1, T0)), default);
        var failing = new RecordingPoster { Refuse = (_, _) => "cannot reach https://acme.kcap.ai" };
        await Assert.That(await (await ScopeAsync(failing)).RemoveAsync("chat-main", default)).IsEqualTo(1);
        var aSession = State().Open!.SessionId;

        // Account B grants the same chat and records: its own directory, its own session.
        var b = new RecordingPoster();
        await (await ScopeAsync(b, "user_01OTHER")).AddAsync("chat-main", History, default);
        await Assert.That(await (await IngestAsync(b, "user_01OTHER")).RunAsync(Walk("b1", T0 + Minute, User(1, T0)), default)).IsEqualTo(0);
        await Assert.That(b.Starts.Single()).IsNotEqualTo(aSession);
        await Assert.That(b.Ends).IsEmpty();
        await Assert.That(await (await ScopeAsync(b, "user_01OTHER")).SweepAsync(default)).IsEqualTo(0);
        await Assert.That(b.Ends).IsEmpty();                                                       // B's sweep never touches A's intent

        // A signs back in: sweep completes A's closure.
        var back = new RecordingPoster();
        await Assert.That(await (await ScopeAsync(back)).SweepAsync(default)).IsEqualTo(0);
        await Assert.That(back.Ends).IsEquivalentTo([aSession]);
        await Assert.That(Store(Acme with { UserId = "user_01OTHER" }).LoadState("chat-main").State!.Open).IsNotNull();
    }

    [Test]
    [NotInParallel]
    public async Task Sweep_prints_active_chats_with_key_watermark_and_boundary_and_never_idle_closes() {
        var poster = await Record(async scope => await scope.AddAsync("chat-main", After(0), default));
        await (await IngestAsync(poster)).RunAsync(Walk("w1", T0 + Minute, User(1, T0)), default);
        _time.SetUtcNow(DateTimeOffset.FromUnixTimeMilliseconds(T0 + 10 * Hour));
        using var stdout = ConsoleOutput.StartCapture("\n");

        var sweeping = new RecordingPoster();
        await Assert.That(await (await ScopeAsync(sweeping)).SweepAsync(default)).IsEqualTo(0);

        await Assert.That(sweeping.Accepted).IsEmpty();
        await Assert.That(stdout.GetCapturedOutput().Trim())
            .IsEqualTo($"key={MuseAssistantPaths.ChatKey("chat-main")} watermark=1 boundary=after_seq after_seq=0 chat=chat-main");
    }

    [Test]
    [NotInParallel]
    public async Task Sweep_exits_3_naming_each_listed_chat_without_usable_state_and_still_prints_the_active_ones() {
        var poster = await Record(async scope => { await scope.AddAsync("chat-a", History, default); await scope.AddAsync("chat-b", History, default); });
        var dir = MuseAssistantPaths.ScopeDir(Config.Root, Acme);
        File.WriteAllText(MuseAssistantPaths.ChatState(dir, "chat-b"), "{ not json");
        var store = Store();
        store.SaveScope(store.LoadScope() with { Chats = [.. store.LoadScope().Chats, "chat-c d"] });
        using var output = ConsoleOutput.StartFullCapture("\n");

        await Assert.That(await (await ScopeAsync(new RecordingPoster())).SweepAsync(default)).IsEqualTo(3);

        await Assert.That(output.GetCapturedOutput().Trim()).IsEqualTo(
            $"key={MuseAssistantPaths.ChatKey("chat-a")} watermark=none boundary=history after_seq=none chat=chat-a");
        await Assert.That(output.GetCapturedError()).Contains("no usable state; the state file is unreadable or belongs to another account; `scope remove` sets it aside, then `scope add` records the chat afresh chat=chat-b");
        await Assert.That(output.GetCapturedError()).Contains("no usable state; run `scope remove` then `scope add` chat=chat-c d");
        await Assert.That(output.GetCapturedError()).DoesNotContain("chat=chat-a");
    }

    [Test]
    public async Task Reset_revokes_every_chat_posts_the_closures_it_can_and_clears_the_list() {
        var poster = await Record(async scope => { await scope.AddAsync("chat-a", History, default); await scope.AddAsync("chat-b", History, default); });
        await (await IngestAsync(poster)).RunAsync(new WalkBuilder(Walks) { ChatId = "chat-a", FetchedAtMs = T0 + Minute }.AddPage(null, null, false, User(1, T0)).Write("a"), default);
        var flaky = new RecordingPoster { Refuse = (a, _) => a is MuseAssistantEndSession e && e.Session.SessionId == poster.Starts.Single() ? "hooks/session-end/muse-assistant answered 503" : null };

        await Assert.That(await (await ScopeAsync(flaky)).ResetAsync(default)).IsEqualTo(1);

        await Assert.That(Store().LoadScope().Chats).IsEmpty();
        await Assert.That(Store().EnumerateStates().All(s => s.Grant.Revoked)).IsTrue();
        await Assert.That(State("chat-a").Open!.Phase).IsEqualTo(MuseAssistantSessionPhases.PendingEnd);

        var sweeping = new RecordingPoster();
        await Assert.That(await (await ScopeAsync(sweeping)).SweepAsync(default)).IsEqualTo(0);
        await Assert.That(State("chat-a").Open).IsNull();
    }

    [Test]
    public async Task State_loss_after_two_grants_then_a_re_grant_records_only_above_the_new_boundary_under_a_third_id() {
        var poster = await Record(async scope => await scope.AddAsync("chat-main", History, default));
        await (await IngestAsync(poster)).RunAsync(Walk("w1", T0 + Minute, User(1, T0)), default);
        var scope = await ScopeAsync(poster);
        await scope.RemoveAsync("chat-main", default);
        await scope.AddAsync("chat-main", After(1), default);
        await (await IngestAsync(poster)).RunAsync(Walk("w2", T0 + 3 * Minute, User(1, T0), User(2, T0 + 2 * Minute)), default);
        var seen = poster.Starts.Distinct().ToList();

        Directory.Delete(Path.Join(Config.Directory, MuseAssistantPaths.Root), recursive: true);   // "Reset Muse" wiped the home
        await scope.AddAsync("chat-main", After(3), default);
        var fresh = new RecordingPoster();
        await (await IngestAsync(fresh)).RunAsync(Walk("w3", T0 + 6 * Minute, User(1, T0), User(2, T0 + 2 * Minute), User(3, T0 + 4 * Minute), User(4, T0 + 5 * Minute)), default);

        await Assert.That(fresh.Seqs).IsEquivalentTo([4]);
        await Assert.That(seen).DoesNotContain(fresh.Starts.Single());
    }

    [Test]
    [NotInParallel]
    [Arguments((object)new[] { "--chat", "chat-main" })]
    [Arguments((object)new[] { "--chat", "chat-main", "--after-seq" })]
    [Arguments((object)new[] { "--chat", "chat-main", "--after-seq", "-5" })]
    [Arguments((object)new[] { "--chat", "chat-main", "--after-seq", "twelve" })]
    [Arguments((object)new[] { "--chat", "chat-main", "--after-seq", "3", "--include-history" })]
    [Arguments((object)new[] { "--chat", "chat-main", "--empty-chat", "--include-history" })]
    public async Task Add_refuses_a_malformed_or_ambiguous_boundary_without_writing(string[] args) {
        var poster = new RecordingPoster();
        var scope  = await ScopeAsync(poster);
        var root   = Path.Join(Config.Directory, MuseAssistantPaths.Root);

        using (var stderr = ConsoleOutput.StartErrorCapture()) {
            await Assert.That(await scope.AddAsync("chat-main", args, default)).IsEqualTo(2);
            await Assert.That(stderr.GetCapturedError()).Contains(MuseAssistantBoundary.Usage);
        }

        await Assert.That(Directory.Exists(root)).IsFalse();
        await Assert.That(poster.Accepted).IsEmpty();

        // The same entry writes once the boundary is well formed, so the absence above is the refusal's doing.
        await Assert.That(await scope.AddAsync("chat-main", ["--chat", "chat-main", "--empty-chat"], default)).IsEqualTo(0);
        await Assert.That(Store().LoadScope().Chats).IsEquivalentTo(["chat-main"]);
    }

    [Test]
    [NotInParallel]
    public async Task Add_empty_chat_on_a_revoked_chat_that_has_a_watermark_is_refused_and_leaves_the_revocation_alone() {
        var poster = await Record(async scope => await scope.AddAsync("chat-main", History, default));
        await (await IngestAsync(poster)).RunAsync(Walk("w1", T0 + Minute, User(1, T0)), default);
        var scope = await ScopeAsync(poster);
        await scope.RemoveAsync("chat-main", default);
        var revoked = State();
        using var stderr = ConsoleOutput.StartErrorCapture();

        await Assert.That(await scope.AddAsync("chat-main", EmptyChat, default)).IsEqualTo(2);

        await Assert.That(stderr.GetCapturedError()).Contains("--empty-chat");
        await Assert.That(State()).IsEqualTo(revoked);
        await Assert.That(Store().LoadScope().Chats).IsEmpty();
    }

    [Test]
    public async Task Add_empty_chat_on_a_revoked_chat_that_never_recorded_anything_is_granted() {
        var scope = await ScopeAsync(new RecordingPoster());
        await scope.AddAsync("chat-main", History, default);
        await scope.RemoveAsync("chat-main", default);

        await Assert.That(await scope.AddAsync("chat-main", EmptyChat, default)).IsEqualTo(0);

        await Assert.That(State().Grant.Revoked).IsFalse();
        await Assert.That(State().Grant.Boundary).IsEqualTo(MuseAssistantGrantBoundaries.EmptyChat);
    }

    [Test]
    [NotInParallel]
    public async Task Sweep_does_not_wait_on_a_busy_chat_lock_and_still_prints_the_busy_chat_only_while_it_is_granted() {
        var poster = await Record(async scope => { await scope.AddAsync("chat-a", History, default); await scope.AddAsync("chat-b", History, default); await scope.AddAsync("chat-c", History, default); });
        await (await ScopeAsync(poster)).RemoveAsync("chat-b", default);
        var dir = MuseAssistantPaths.ScopeDir(Config.Root, Acme);
        using var output = ConsoleOutput.StartFullCapture("\n");

        // The system clock would let a waiting sweep run for the full lock wait; the test bounds it far below.
        var sweeping = await ScopeAsync(poster, time: TimeProvider.System);
        using (MuseAssistantChatLock.TryAcquire(MuseAssistantPaths.ChatLock(dir, "chat-a")))
        using (MuseAssistantChatLock.TryAcquire(MuseAssistantPaths.ChatLock(dir, "chat-b")))
            await Assert.That(await sweeping.SweepAsync(default).WaitAsync(TimeSpan.FromSeconds(10))).IsEqualTo(0);

        var lines = output.GetCapturedOutput().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        await Assert.That(lines.Select(l => l[(l.IndexOf("chat=", StringComparison.Ordinal) + 5)..])).IsEquivalentTo(["chat-a", "chat-c"]);
        await Assert.That(output.GetCapturedError()).Contains("chat=chat-a");
        await Assert.That(output.GetCapturedError()).Contains("chat=chat-b");
    }

    [Test]
    [NotInParallel]
    public async Task Reset_names_only_the_still_granted_chats_on_its_busy_lines() {
        var poster = await Record(async scope => { await scope.AddAsync("chat-a", History, default); await scope.AddAsync("chat-b", History, default); });
        await (await ScopeAsync(poster)).RemoveAsync("chat-b", default);
        var dir = MuseAssistantPaths.ScopeDir(Config.Root, Acme);
        using var stderr = ConsoleOutput.StartErrorCapture();

        using (MuseAssistantChatLock.TryAcquire(MuseAssistantPaths.ChatLock(dir, "chat-a")))
        using (MuseAssistantChatLock.TryAcquire(MuseAssistantPaths.ChatLock(dir, "chat-b")))
            await Assert.That(await TimedOut((await ScopeAsync(poster)).ResetAsync(default))).IsEqualTo(4);

        await Assert.That(stderr.GetCapturedError()).Contains("chat=chat-a");
        await Assert.That(stderr.GetCapturedError()).DoesNotContain("chat=chat-b");
    }

    [Test]
    [NotInParallel]
    public async Task Reset_marks_a_chat_with_chat_equals_only_when_it_may_still_be_recording() {
        var poster = await Record(async scope => { await scope.AddAsync("chat-a", History, default); await scope.AddAsync("chat-b", History, default); });
        await (await IngestAsync(poster)).RunAsync(new WalkBuilder(Walks) { ChatId = "chat-a", FetchedAtMs = T0 + Minute }.AddPage(null, null, false, User(1, T0)).Write("a"), default);
        var flaky = new RecordingPoster { Refuse = (a, _) => a is MuseAssistantEndSession ? "hooks/session-end/muse-assistant answered 503" : null };
        var dir   = MuseAssistantPaths.ScopeDir(Config.Root, Acme);
        using var stderr = ConsoleOutput.StartErrorCapture("\n");

        using (MuseAssistantChatLock.TryAcquire(MuseAssistantPaths.ChatLock(dir, "chat-b")))
            await Assert.That(await TimedOut((await ScopeAsync(flaky)).ResetAsync(default))).IsEqualTo(4);

        await Assert.That(State("chat-a").Grant.Revoked).IsTrue();
        await Assert.That(stderr.GetCapturedError()).Contains("chat=chat-b");
        await Assert.That(stderr.GetCapturedError()).DoesNotContain("chat=chat-a");
        await Assert.That(stderr.GetCapturedError()).Contains("\"chat-a\"");
    }

    [Test]
    public async Task Remove_that_cannot_take_the_chat_lock_revokes_nothing_and_exits_4() {
        var poster = await Record(async scope => await scope.AddAsync("chat-main", History, default));
        await (await IngestAsync(poster)).RunAsync(Walk("w1", T0 + Minute, User(1, T0)), default);
        var dir = MuseAssistantPaths.ScopeDir(Config.Root, Acme);

        using (MuseAssistantChatLock.TryAcquire(MuseAssistantPaths.ChatLock(dir, "chat-main")))
            await Assert.That(await TimedOut((await ScopeAsync(poster)).RemoveAsync("chat-main", default))).IsEqualTo(4);

        await Assert.That(State().Grant.Revoked).IsFalse();
        await Assert.That(State().Open!.Phase).IsEqualTo(MuseAssistantSessionPhases.Started);
        await Assert.That(Store().LoadScope().Chats).IsEquivalentTo(["chat-main"]);
        await Assert.That(poster.Ends).IsEmpty();
    }

    [Test]
    [NotInParallel]
    public async Task Remove_of_a_chat_already_revoked_and_closing_while_an_ingest_holds_the_lock_exits_1_not_4() {
        var poster = await Record(async scope => await scope.AddAsync("chat-main", History, default));
        await (await IngestAsync(poster)).RunAsync(Walk("w1", T0 + Minute, User(1, T0)), default);
        var unreachable = new RecordingPoster { Refuse = (_, _) => "cannot reach https://acme.kcap.ai" };
        await Assert.That(await (await ScopeAsync(unreachable)).RemoveAsync("chat-main", default)).IsEqualTo(1);
        var dir = MuseAssistantPaths.ScopeDir(Config.Root, Acme);

        using var stderr = ConsoleOutput.StartErrorCapture();
        using (MuseAssistantChatLock.TryAcquire(MuseAssistantPaths.ChatLock(dir, "chat-main")))
            await Assert.That(await TimedOut((await ScopeAsync(poster)).RemoveAsync("chat-main", default))).IsEqualTo(1);

        await Assert.That(stderr.GetCapturedError()).Contains("revoked; the end is still being delivered");
        await Assert.That(State().Open!.Phase).IsEqualTo(MuseAssistantSessionPhases.PendingEnd);
        await Assert.That(poster.Ends).IsEmpty();
    }

    [Test]
    [NotInParallel]
    public async Task Remove_of_a_chat_already_revoked_with_nothing_left_to_deliver_exits_0_while_an_ingest_holds_the_lock() {
        var poster = await Record(async scope => await scope.AddAsync("chat-main", History, default));
        await (await IngestAsync(poster)).RunAsync(Walk("w1", T0 + Minute, User(1, T0)), default);
        await Assert.That(await (await ScopeAsync(poster)).RemoveAsync("chat-main", default)).IsEqualTo(0);
        var dir = MuseAssistantPaths.ScopeDir(Config.Root, Acme);

        using var stderr = ConsoleOutput.StartErrorCapture();
        using (MuseAssistantChatLock.TryAcquire(MuseAssistantPaths.ChatLock(dir, "chat-main")))
            await Assert.That(await TimedOut((await ScopeAsync(poster)).RemoveAsync("chat-main", default))).IsEqualTo(0);

        await Assert.That(stderr.GetCapturedError()).Contains("already revoked");
        await Assert.That(Store().LoadScope().Chats).IsEmpty();
    }

    [Test]
    [NotInParallel]
    public async Task A_busy_chat_lock_names_the_ingest_and_a_busy_scope_lock_names_the_other_scope_command() {
        var poster = await Record(async scope => await scope.AddAsync("chat-main", History, default));
        var dir    = MuseAssistantPaths.ScopeDir(Config.Root, Acme);

        using (var stderr = ConsoleOutput.StartErrorCapture()) {
            using (MuseAssistantChatLock.TryAcquire(MuseAssistantPaths.ChatLock(dir, "chat-main")))
                await Assert.That(await TimedOut((await ScopeAsync(poster)).AddAsync("chat-main", History, default))).IsEqualTo(1);

            await Assert.That(stderr.GetCapturedError()).Contains("ingest in progress, retry chat=chat-main");
        }

        using (var stderr = ConsoleOutput.StartErrorCapture()) {
            using (MuseAssistantChatLock.TryAcquire(MuseAssistantPaths.ScopeLock(dir)))
                await Assert.That(await TimedOut((await ScopeAsync(poster)).AddAsync("chat-main", History, default))).IsEqualTo(1);

            await Assert.That(stderr.GetCapturedError()).Contains("another scope command or sweep is running, retry");
            await Assert.That(stderr.GetCapturedError()).DoesNotContain("ingest");
        }
    }

    [Test]
    [NotInParallel]
    public async Task Reset_sets_aside_every_unusable_state_file_and_exits_3_rather_than_claiming_every_grant_revoked() {
        var scope = await ScopeAsync(new RecordingPoster());
        await scope.AddAsync("chat-a", History, default);
        await scope.AddAsync("chat-b", History, default);
        var dir      = MuseAssistantPaths.ScopeDir(Config.Root, Acme);
        var listed   = MuseAssistantPaths.ChatState(dir, "chat-b");
        var unlisted = MuseAssistantPaths.ChatState(dir, "chat-c");
        File.WriteAllText(listed, "{ not json");
        File.WriteAllText(unlisted, File.ReadAllText(MuseAssistantPaths.ChatState(dir, "chat-a")).Replace("user_01HXYZ", "user_01OTHER"));
        using var output = ConsoleOutput.StartFullCapture("\n");

        await Assert.That(await scope.ResetAsync(default)).IsEqualTo(3);

        await Assert.That(File.Exists(listed)).IsFalse();
        await Assert.That(File.Exists(unlisted)).IsFalse();
        var aside = Directory.GetFiles(dir, Path.GetFileName(listed) + ".unusable-*").Single();
        await Assert.That(File.ReadAllText(aside)).IsEqualTo("{ not json");
        await Assert.That(Directory.GetFiles(dir, Path.GetFileName(unlisted) + ".unusable-*")).Count().IsEqualTo(1);
        await Assert.That(State("chat-a").Grant.Revoked).IsTrue();
        await Assert.That(Store().LoadScope().Chats).IsEmpty();
        await Assert.That(output.GetCapturedOutput()).Contains("reset: every grant revoked or set aside");
        await Assert.That(output.GetCapturedError()).Contains(Path.GetFileName(aside)).And.Contains("\"chat-b\"").And.DoesNotContain("chat=");

        await Assert.That(await (await ScopeAsync(new RecordingPoster())).SweepAsync(default)).IsEqualTo(0);
    }

    [Test]
    [NotInParallel]
    public async Task Reset_of_a_listed_chat_with_no_state_file_unlists_it_and_exits_3() {
        var poster = new RecordingPoster();
        var scope  = await ScopeAsync(poster);
        await scope.AddAsync("chat-a", History, default);
        await scope.AddAsync("chat-b", History, default);
        File.Delete(MuseAssistantPaths.ChatState(MuseAssistantPaths.ScopeDir(Config.Root, Acme), "chat-b"));
        using var output = ConsoleOutput.StartFullCapture("\n");

        await Assert.That(await scope.ResetAsync(default)).IsEqualTo(3);

        await Assert.That(State("chat-a").Grant.Revoked).IsTrue();
        await Assert.That(Store().LoadScope().Chats).IsEmpty();
        await Assert.That(output.GetCapturedOutput()).Contains("reset: every grant revoked or set aside");
        await Assert.That(output.GetCapturedError()).Contains("no grant recorded for \"chat-b\"").And.Contains("may stay open").And.DoesNotContain("chat=");

        await Assert.That(await scope.ResetAsync(default)).IsEqualTo(0);
    }

    [Test]
    [NotInParallel]
    public async Task Reset_that_cannot_lock_a_listed_chat_with_no_state_file_keeps_it_listed_and_exits_4() {
        var scope = await ScopeAsync(new RecordingPoster());
        await scope.AddAsync("chat-b", History, default);
        var dir = MuseAssistantPaths.ScopeDir(Config.Root, Acme);
        File.Delete(MuseAssistantPaths.ChatState(dir, "chat-b"));
        using var stderr = ConsoleOutput.StartErrorCapture("\n");

        using (MuseAssistantChatLock.TryAcquire(MuseAssistantPaths.ChatLock(dir, "chat-b")))
            await Assert.That(await TimedOut(scope.ResetAsync(default))).IsEqualTo(4);

        await Assert.That(Store().LoadScope().Chats).IsEquivalentTo(["chat-b"]);
        await Assert.That(stderr.GetCapturedError()).Contains("chat=chat-b");
    }

    [Test]
    [NotInParallel]
    public async Task Reset_revokes_a_listed_chat_whose_state_file_reappears_before_its_lock_is_taken() {
        var poster = await Record(async scope => await scope.AddAsync("chat-b", History, default));
        var dir    = MuseAssistantPaths.ScopeDir(Config.Root, Acme);
        var saved  = State("chat-b");
        File.Delete(MuseAssistantPaths.ChatState(dir, "chat-b"));
        using var output = ConsoleOutput.StartFullCapture("\n");

        var clock = new TimerSignallingTimeProvider(_time.GetUtcNow());
        Task<int> reset;
        using (MuseAssistantChatLock.TryAcquire(MuseAssistantPaths.ChatLock(dir, "chat-b"))) {
            reset = (await ScopeAsync(poster, time: clock)).ResetAsync(default);
            await clock.Waiting.WaitAsync(TimeSpan.FromSeconds(10));   // reset has taken its snapshot and is waiting on the chat lock
            Store().SaveState(saved);
        }

        for (var i = 0; !reset.IsCompleted && i++ < 1000;) { clock.Advance(TimeSpan.FromMilliseconds(250)); await Task.Delay(1); }

        await Assert.That(await reset.WaitAsync(TimeSpan.FromSeconds(10))).IsEqualTo(0);

        await Assert.That(State("chat-b").Grant.Revoked).IsTrue();
        await Assert.That(Store().LoadScope().Chats).IsEmpty();
        await Assert.That(output.GetCapturedError()).DoesNotContain("no grant recorded");
    }

    [Test]
    public async Task Reset_that_cannot_lock_a_listed_chat_with_unusable_state_keeps_it_listed_and_exits_4() {
        var scope = await ScopeAsync(new RecordingPoster());
        await scope.AddAsync("chat-b", History, default);
        var dir  = MuseAssistantPaths.ScopeDir(Config.Root, Acme);
        var path = MuseAssistantPaths.ChatState(dir, "chat-b");
        File.WriteAllText(path, "{ not json");

        using (MuseAssistantChatLock.TryAcquire(MuseAssistantPaths.ChatLock(dir, "chat-b")))
            await Assert.That(await TimedOut(scope.ResetAsync(default))).IsEqualTo(4);

        await Assert.That(File.ReadAllText(path)).IsEqualTo("{ not json");
        await Assert.That(Store().LoadScope().Chats).IsEquivalentTo(["chat-b"]);
    }

    [Test]
    public async Task Reset_that_cannot_take_a_chat_lock_keeps_that_chat_granted_and_listed_and_exits_4() {
        var poster = await Record(async scope => { await scope.AddAsync("chat-a", History, default); await scope.AddAsync("chat-b", History, default); });
        var dir    = MuseAssistantPaths.ScopeDir(Config.Root, Acme);

        using (MuseAssistantChatLock.TryAcquire(MuseAssistantPaths.ChatLock(dir, "chat-a")))
            await Assert.That(await TimedOut((await ScopeAsync(poster)).ResetAsync(default))).IsEqualTo(4);

        await Assert.That(State("chat-a").Grant.Revoked).IsFalse();
        await Assert.That(State("chat-b").Grant.Revoked).IsTrue();
        await Assert.That(Store().LoadScope().Chats).IsEquivalentTo(["chat-a"]);
    }

    [Test]
    public async Task Remove_and_reset_without_a_sign_in_revoke_nothing_and_exit_4() {
        var store = Store();
        store.SaveState(new("chat-main", Acme.ServerUrl, Acme.UserId, new("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", MuseAssistantGrantBoundaries.History, null, T0, null), null, null, null));
        store.SaveScope(store.LoadScope() with { Chats = ["chat-main"] });
        var poster    = new RecordingPoster();
        var signedOut = new MuseAssistantScope(
            Config.Root, new MuseAssistantIdentity(Resolutions.At(Acme.ServerUrl, Config.Root), AuthFixtures.NewTokenStore(Config.Root)),
            new(), new MuseAssistantActionRunner(poster, _time), _time);

        await Assert.That(await signedOut.RemoveAsync("chat-main", default)).IsEqualTo(4);
        await Assert.That(await signedOut.ResetAsync(default)).IsEqualTo(4);

        await Assert.That(State().Grant.Revoked).IsFalse();
        await Assert.That(Store().LoadScope().Chats).IsEquivalentTo(["chat-main"]);
        await Assert.That(poster.Accepted).IsEmpty();
    }

    [Test]
    public async Task A_sweep_signed_into_another_tenant_never_posts_this_tenants_intents() {
        const string Other = "https://other.kcap.ai";
        var acme = await Record(async scope => await scope.AddAsync("chat-main", History, default));
        await (await IngestAsync(acme)).RunAsync(Walk("a1", T0 + Minute, User(1, T0)), default);
        var failing = new RecordingPoster { Refuse = (_, _) => "cannot reach https://acme.kcap.ai" };
        await Assert.That(await (await ScopeAsync(failing)).RemoveAsync("chat-main", default)).IsEqualTo(1);
        var acmeSession = State().Open!.SessionId;

        // The same user on another tenant grants the same chat and sweeps there.
        var other = new RecordingPoster();
        await (await ScopeAsync(other, serverUrl: Other)).AddAsync("chat-main", History, default);
        await Assert.That(await (await IngestAsync(other, serverUrl: Other)).RunAsync(Walk("o1", T0 + Minute, User(1, T0)), default)).IsEqualTo(0);
        await Assert.That(await (await ScopeAsync(other, serverUrl: Other)).SweepAsync(default)).IsEqualTo(0);

        await Assert.That(other.Ends).IsEmpty();
        await Assert.That(other.Starts.Single()).IsNotEqualTo(acmeSession);
        await Assert.That(State().Open!.Phase).IsEqualTo(MuseAssistantSessionPhases.PendingEnd);

        var back = new RecordingPoster();
        await Assert.That(await (await ScopeAsync(back)).SweepAsync(default)).IsEqualTo(0);
        await Assert.That(back.Ends).IsEquivalentTo([acmeSession]);
        await Assert.That(Store(Acme with { ServerUrl = Other }).LoadState("chat-main").State!.Open).IsNotNull();
    }

    [Test]
    [NotInParallel]
    public async Task Show_lists_every_chat_with_its_status_and_posts_nothing() {
        var poster = await Record(async scope => { await scope.AddAsync("chat-a", History, default); await scope.AddAsync("chat-b", After(3), default); });
        await (await IngestAsync(poster)).RunAsync(new WalkBuilder(Walks) { ChatId = "chat-b", FetchedAtMs = T0 + Minute }.AddPage(null, null, false, User(4, T0)).Write("b"), default);
        await (await ScopeAsync(new RecordingPoster { Refuse = (_, _) => "hooks/session-end/muse-assistant answered 503" })).RemoveAsync("chat-b", default);
        using var stdout = ConsoleOutput.StartCapture("\n");

        var showing = new RecordingPoster();
        await Assert.That(await (await ScopeAsync(showing)).ShowAsync(default)).IsEqualTo(0);

        await Assert.That(showing.Accepted).IsEmpty();
        var lines = stdout.GetCapturedOutput().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        await Assert.That(lines.Single(l => l.EndsWith("chat=chat-a", StringComparison.Ordinal))).Contains("status=active");
        await Assert.That(lines.Single(l => l.EndsWith("chat=chat-b", StringComparison.Ordinal))).Contains("status=closing");
    }

    [Test]
    [NotInParallel]
    [Arguments("{ not json")]
    [Arguments("")]
    public async Task Remove_of_an_unreadable_state_sets_the_file_aside_and_a_later_add_grants_afresh(string content) {
        var scope = await ScopeAsync(new RecordingPoster());
        await scope.AddAsync("chat-main", History, default);
        var dir  = MuseAssistantPaths.ScopeDir(Config.Root, Acme);
        var path = MuseAssistantPaths.ChatState(dir, "chat-main");
        File.WriteAllText(path, content);
        using var output = ConsoleOutput.StartFullCapture("\n");

        await Assert.That(await scope.RemoveAsync("chat-main", default)).IsEqualTo(3);

        await Assert.That(File.Exists(path)).IsFalse();
        var aside = Directory.GetFiles(dir, Path.GetFileName(path) + ".unusable-*").Single();
        await Assert.That(File.ReadAllText(aside)).IsEqualTo(content);
        await Assert.That(Path.GetFileName(aside)).Matches($"^{Path.GetFileName(path)}\\.unusable-{T0 + 4 * Hour}$");
        await Assert.That(Store().LoadScope().Chats).IsEmpty();
        await Assert.That(Store().EnumerateStates()).IsEmpty();
        await Assert.That(output.GetCapturedError()).Contains("set aside").And.Contains(Path.GetFileName(aside));

        await Assert.That(await scope.AddAsync("chat-main", History, default)).IsEqualTo(0);
        await Assert.That(State().Grant.Id).Matches("^[0-9a-f]{32}$");
        await Assert.That(Store().LoadScope().Chats).IsEquivalentTo(["chat-main"]);
        await Assert.That(File.Exists(aside)).IsTrue();
    }

    [Test]
    [NotInParallel]
    public async Task Remove_of_a_state_file_that_belongs_to_another_account_sets_it_aside_and_a_later_add_grants_afresh() {
        var scope = await ScopeAsync(new RecordingPoster());
        await scope.AddAsync("chat-main", History, default);
        var before = State().Grant.Id;
        var dir    = MuseAssistantPaths.ScopeDir(Config.Root, Acme);
        var path   = MuseAssistantPaths.ChatState(dir, "chat-main");
        var copied = File.ReadAllText(path).Replace("user_01HXYZ", "user_01OTHER");
        File.WriteAllText(path, copied);
        using var output = ConsoleOutput.StartFullCapture("\n");

        await Assert.That(await scope.RemoveAsync("chat-main", default)).IsEqualTo(3);

        await Assert.That(File.Exists(path)).IsFalse();
        await Assert.That(File.ReadAllText(Directory.GetFiles(dir, Path.GetFileName(path) + ".unusable-*").Single())).IsEqualTo(copied);
        await Assert.That(Store().LoadScope().Chats).IsEmpty();

        await Assert.That(await scope.AddAsync("chat-main", History, default)).IsEqualTo(0);
        await Assert.That(State().Grant.Id).IsNotEqualTo(before);
    }

    /// <summary>A listed chat whose state file is gone may have a session open on the server that nothing
    /// can close now, so the unlisting is not reported as a clean stop.</summary>
    [Test]
    [NotInParallel]
    public async Task Remove_of_a_listed_chat_with_no_state_file_unlists_it_and_exits_3() {
        var poster = new RecordingPoster();
        var scope  = await ScopeAsync(poster);
        await scope.AddAsync("chat-main", History, default);
        var dir = MuseAssistantPaths.ScopeDir(Config.Root, Acme);
        File.Delete(MuseAssistantPaths.ChatState(dir, "chat-main"));
        using var output = ConsoleOutput.StartFullCapture("\n");

        await Assert.That(await scope.RemoveAsync("chat-main", default)).IsEqualTo(3);

        await Assert.That(Directory.GetFiles(dir, "*.unusable-*")).IsEmpty();
        await Assert.That(Store().LoadScope().Chats).IsEmpty();
        await Assert.That(poster.Accepted).IsEmpty();
        await Assert.That(output.GetCapturedOutput()).IsEmpty();
        await Assert.That(output.GetCapturedError()).Contains("no grant recorded").And.Contains("may stay open").And.Contains("chat=chat-main");

        await Assert.That(await scope.RemoveAsync("chat-main", default)).IsEqualTo(0);   // retried: nothing listed, nothing to stop
    }

    [Test]
    [NotInParallel]
    public async Task Remove_of_a_chat_never_granted_exits_0() {
        var scope = await ScopeAsync(new RecordingPoster());
        using var output = ConsoleOutput.StartFullCapture("\n");

        await Assert.That(await scope.RemoveAsync("chat-main", default)).IsEqualTo(0);

        await Assert.That(output.GetCapturedOutput()).Contains("no grant recorded chat=chat-main");
        await Assert.That(Store().LoadScope().Chats).IsEmpty();
    }

    async Task<RecordingPoster> Record(Func<MuseAssistantScope, Task> act) {
        var poster = new RecordingPoster();
        await act(await ScopeAsync(poster));
        return poster;
    }
}
