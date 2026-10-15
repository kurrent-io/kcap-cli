using Capacitor.Cli.Harness.MuseAssistant;
using TUnit.Assertions.Enums;
using static Capacitor.Cli.Tests.Unit.Harness.MuseAssistant.WalkBuilder;

namespace Capacitor.Cli.Tests.Unit.Harness.MuseAssistant;

/// <summary>The ingest end to end against a recording server double: a walk becomes posts, state advances
/// only on acceptance, every refusal and crash leaves a state a rerun completes without duplicates, and
/// anything out of scope posts nothing.</summary>
public class MuseAssistantIngestTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }
    [TempHome]       public required TempHome       Home   { get; init; }
    [TempDir]        public required TempDir        Walks  { get; init; }

    const long Minute = 60_000, Hour = 3_600_000, T0 = 1_760_000_000_000;
    const string GrantId = "0123456789abcdef0123456789abcdef";
    static readonly MuseAssistantPrincipal Acme = new("https://acme.kcap.ai", "user_01HXYZ");

    MuseAssistantStateStore Store(MuseAssistantPrincipal? p = null) => new(MuseAssistantPaths.ScopeDir(Config.Root, p ?? Acme), p ?? Acme);

    void Grant(string chat = "chat-main", string boundary = MuseAssistantGrantBoundaries.History, long? afterSeq = null, MuseAssistantPrincipal? p = null) {
        var store = Store(p);
        store.SaveState(new(chat, (p ?? Acme).ServerUrl, (p ?? Acme).UserId, new(GrantId, boundary, afterSeq, T0 - Hour, null), null, null, null));
        store.SaveScope(store.LoadScope() with { Chats = [.. store.LoadScope().Chats.Where(c => c != chat), chat] });
    }

    string StateRoot => Path.Join(Config.Directory, MuseAssistantPaths.Root);

    void ResetState() { if (Directory.Exists(StateRoot)) Directory.Delete(StateRoot, recursive: true); }

    async Task<MuseAssistantIngest> IngestAsync(RecordingPoster poster, MuseAssistantStateStoreFactory? stores = null, string userId = "user_01HXYZ") =>
        new(Config.Root, Home.Home, await MuseAssistantTestIdentity.SignedInAsync(Config.Root, userId: userId), stores ?? new(), new MuseAssistantActionRunner(poster, TimeProvider.System), TimeProvider.System);

    string TwoSessionWalk(string name = "walk", long fetchedAt = T0 + 6 * Hour, Action<TempDirHandle>? tamper = null) => new WalkBuilder(Walks) { FetchedAtMs = fetchedAt }
        .AddPage(null, "c1", true, User(5, T0 + 3 * Hour, turn: "t2"), Assistant(6, T0 + 3 * Hour + Minute, turn: "t2"))
        .AddPage("c1", null, false, User(1, T0), Assistant(2, T0 + Minute))
        .Write(name, tamper);

    static string Id(long firstSeq, string user = "user_01HXYZ") => MuseAssistantSessionId.For(user, "chat-main", GrantId, firstSeq);

    [Test]
    public async Task A_walk_opens_sessions_sends_items_closes_idle_ones_and_advances_the_watermark() {
        Grant();
        var poster = new RecordingPoster();

        var exit = await (await IngestAsync(poster)).RunAsync(TwoSessionWalk(), default);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(poster.Kinds).IsEquivalentTo([
            nameof(MuseAssistantStartSession), nameof(MuseAssistantSendLines), nameof(MuseAssistantEndSession),
            nameof(MuseAssistantStartSession), nameof(MuseAssistantSendLines), nameof(MuseAssistantEndSession)], CollectionOrdering.Matching);
        await Assert.That(poster.Starts).IsEquivalentTo([Id(1), Id(5)], CollectionOrdering.Matching);
        await Assert.That(poster.Seqs).IsEquivalentTo([1, 2, 5, 6], CollectionOrdering.Matching);
        var state = Store().LoadState("chat-main").State!;
        await Assert.That(state.Watermark).IsEqualTo(6);
        await Assert.That(state.Open).IsNull();
        await Assert.That(state.LastSelectedAtMs).IsEqualTo(T0 + 3 * Hour + Minute);
        await Assert.That(Directory.Exists(Walks.PathTo("walk"))).IsTrue();
    }

    [Test]
    public async Task A_second_ingest_of_the_same_walk_posts_nothing() {
        Grant();
        await (await IngestAsync(new RecordingPoster())).RunAsync(TwoSessionWalk(), default);
        var poster = new RecordingPoster();

        var exit = await (await IngestAsync(poster)).RunAsync(TwoSessionWalk("again"), default);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(poster.Accepted).IsEmpty();
    }

    [Test]
    public async Task A_developer_only_walk_advances_the_watermark_and_posts_nothing() {
        Grant();
        var poster = new RecordingPoster();
        var dir    = new WalkBuilder(Walks).AddPage(null, null, false, Developer(10, T0), Developer(11, T0 + 1)).Write();

        await Assert.That(await (await IngestAsync(poster)).RunAsync(dir, default)).IsEqualTo(0);
        await Assert.That(poster.Accepted).IsEmpty();
        await Assert.That(Store().LoadState("chat-main").State!.Watermark).IsEqualTo(11);
    }

    [Test]
    public async Task An_empty_chat_grant_records_the_chats_first_message() {
        Grant(boundary: MuseAssistantGrantBoundaries.EmptyChat);
        var poster = new RecordingPoster();
        var dir    = new WalkBuilder(Walks).AddPage(null, null, false, User(1, T0)).Write();

        await Assert.That(await (await IngestAsync(poster)).RunAsync(dir, default)).IsEqualTo(0);
        await Assert.That(poster.Seqs).IsEquivalentTo([1]);
    }

    [Test]
    public async Task Malformed_or_incomplete_input_exits_2_and_posts_nothing() {
        Grant();
        var poster  = new RecordingPoster();
        var ingest  = await IngestAsync(poster);
        var missing = TwoSessionWalk("missing", tamper: d => File.Delete(d.PathTo("page-001.json")));
        var extra   = TwoSessionWalk("extra", tamper: d => d.CreateFile("stray.json", "{}"));
        var partial = new WalkBuilder(Walks).AddPage(null, "c1", true, User(50, T0)).Write("partial");
        var notWalk = Walks.CreateDir("empty");

        await Assert.That(await ingest.RunAsync(missing, default)).IsEqualTo(2);
        await Assert.That(await ingest.RunAsync(extra, default)).IsEqualTo(2);
        await Assert.That(await ingest.RunAsync(partial, default)).IsEqualTo(2);
        await Assert.That(await ingest.RunAsync(notWalk, default)).IsEqualTo(2);
        await Assert.That(poster.Accepted).IsEmpty();
        await Assert.That(Store().LoadState("chat-main").State!.Watermark).IsNull();
    }

    [Test]
    public async Task An_out_of_range_timestamp_exits_2_before_any_session_intent_is_written() {
        Grant();
        var poster = new RecordingPoster();
        var dir    = new WalkBuilder(Walks).AddPage(null, null, false, User(1, T0)).Write(tamper: d =>
            d.CreateFile("page-000.json", File.ReadAllText(d.PathTo("page-000.json")).Replace($"\"occurred_at_ms\":{T0}", $"\"occurred_at_ms\":{long.MaxValue}")));

        await Assert.That(await (await IngestAsync(poster)).RunAsync(dir, default)).IsEqualTo(2);
        await Assert.That(poster.Accepted).IsEmpty();
        await Assert.That(Store().LoadState("chat-main").State!.Open).IsNull();
        await Assert.That(Store().LoadState("chat-main").State!.Watermark).IsNull();
    }

    [Test]
    public async Task An_item_too_large_to_send_exits_2_and_posts_nothing_for_the_walk() {
        Grant();
        var poster = new RecordingPoster();
        var dir    = new WalkBuilder(Walks).AddPage(null, null, false, User(1, T0), User(2, T0 + 1, new string('x', 4 * 1024 * 1024))).Write();

        await Assert.That(await (await IngestAsync(poster)).RunAsync(dir, default)).IsEqualTo(2);
        await Assert.That(poster.Accepted).IsEmpty();
        await Assert.That(Store().LoadState("chat-main").State!.Watermark).IsNull();
    }

    [Test]
    public async Task A_walk_both_malformed_and_out_of_scope_reports_malformed() {
        var dir = TwoSessionWalk("missing", tamper: d => File.Delete(d.PathTo("page-001.json")));

        await Assert.That(await (await IngestAsync(new RecordingPoster())).RunAsync(dir, default)).IsEqualTo(2);
    }

    [Test]
    public async Task Out_of_scope_revoked_missing_state_and_a_copied_state_exit_3_and_post_nothing() {
        var poster = new RecordingPoster();
        var ingest = await IngestAsync(poster);
        var dir    = TwoSessionWalk();

        await Assert.That(await ingest.RunAsync(dir, default)).IsEqualTo(3);                       // nothing granted at all

        var store = Store();
        store.SaveScope(store.LoadScope() with { Chats = ["chat-main"] });
        await Assert.That(await ingest.RunAsync(dir, default)).IsEqualTo(3);                       // listed, no state: never replayed

        Grant();
        var state = store.LoadState("chat-main").State!;
        store.SaveState(state with { Grant = state.Grant with { RevokedAtMs = T0 } });
        await Assert.That(await ingest.RunAsync(dir, default)).IsEqualTo(3);                       // revoked

        var other = Store(Acme with { UserId = "user_01OTHER" });
        other.SaveState(new("chat-main", Acme.ServerUrl, "user_01OTHER", new(GrantId, MuseAssistantGrantBoundaries.History, null, 1, null), null, null, null));
        File.Copy(MuseAssistantPaths.ChatState(other.ScopeDir, "chat-main"), MuseAssistantPaths.ChatState(store.ScopeDir, "chat-main"), overwrite: true);
        await Assert.That(await ingest.RunAsync(dir, default)).IsEqualTo(3);                       // copied from another account

        await Assert.That(poster.Accepted).IsEmpty();
    }

    [Test]
    public async Task A_revoked_chat_with_a_pending_closure_completes_only_that_closure_then_exits_3() {
        Grant();
        await Assert.That(await (await IngestAsync(new RecordingPoster { Refuse = (_, i) => i == 1 ? "hooks/transcript answered 503" : null })).RunAsync(TwoSessionWalk(), default)).IsEqualTo(1);
        var store   = Store();
        var started = store.LoadState("chat-main").State!;
        var open    = started.Open!;
        store.SaveState(started with { Grant = started.Grant with { RevokedAtMs = T0 + Hour }, Open = open with { Phase = MuseAssistantSessionPhases.PendingEnd, EndedAtMs = open.StartedAtMs } });
        var poster = new RecordingPoster();

        await Assert.That(await (await IngestAsync(poster)).RunAsync(TwoSessionWalk("after-revoke"), default)).IsEqualTo(3);

        await Assert.That(poster.Kinds).IsEquivalentTo([nameof(MuseAssistantEndSession)]);
        await Assert.That(poster.Ends).IsEquivalentTo([Id(1)]);
        await Assert.That(store.LoadState("chat-main").State!.Open).IsNull();
    }

    [Test]
    [NotInParallel]
    public async Task Without_a_sign_in_the_ingest_exits_1_naming_kcap_login_and_posts_nothing() {
        Grant();
        var poster    = new RecordingPoster();
        var signedOut = new MuseAssistantIdentity(Resolutions.At(Acme.ServerUrl, Config.Root), AuthFixtures.NewTokenStore(Config.Root));
        var ingest    = new MuseAssistantIngest(Config.Root, Home.Home, signedOut, new(), new MuseAssistantActionRunner(poster, TimeProvider.System), TimeProvider.System);
        using var stderr = ConsoleOutput.StartErrorCapture();

        await Assert.That(await ingest.RunAsync(TwoSessionWalk(), default)).IsEqualTo(1);

        await Assert.That(stderr.GetCapturedError()).Contains("kcap login");
        await Assert.That(poster.Accepted).IsEmpty();
        await Assert.That(Store().LoadState("chat-main").State!.Watermark).IsNull();
    }

    [Test]
    public async Task A_revoked_chat_whose_closure_cannot_be_posted_exits_1_and_keeps_the_closure_pending() {
        Grant();
        await (await IngestAsync(new RecordingPoster { Refuse = (_, i) => i == 1 ? "hooks/transcript answered 503" : null })).RunAsync(TwoSessionWalk(), default);
        var store   = Store();
        var started = store.LoadState("chat-main").State!;
        var open    = started.Open!;
        store.SaveState(started with { Grant = started.Grant with { RevokedAtMs = T0 + Hour }, Open = open with { Phase = MuseAssistantSessionPhases.PendingEnd, EndedAtMs = open.StartedAtMs } });
        var poster = new RecordingPoster { Refuse = (_, _) => "hooks/session-end/muse-assistant answered 503" };

        await Assert.That(await (await IngestAsync(poster)).RunAsync(TwoSessionWalk("after-revoke"), default)).IsEqualTo(1);

        await Assert.That(poster.Accepted).IsEmpty();
        await Assert.That(store.LoadState("chat-main").State!.Open!.Phase).IsEqualTo(MuseAssistantSessionPhases.PendingEnd);

        var retry = new RecordingPoster();
        await Assert.That(await (await IngestAsync(retry)).RunAsync(TwoSessionWalk("retry"), default)).IsEqualTo(3);
        await Assert.That(retry.Ends).IsEquivalentTo([Id(1)]);
    }

    [Test]
    public async Task A_walk_that_is_out_of_scope_and_has_a_coverage_gap_reports_out_of_scope() {
        var poster  = new RecordingPoster();
        var partial = new WalkBuilder(Walks).AddPage(null, "c1", true, User(50, T0)).Write("partial");

        await Assert.That(await (await IngestAsync(poster)).RunAsync(partial, default)).IsEqualTo(3);

        // Once granted, the same walk is refused for its gap, so the 3 above was the scope check, not a missing gap.
        Grant();
        await Assert.That(await (await IngestAsync(poster)).RunAsync(partial, default)).IsEqualTo(2);
        await Assert.That(poster.Accepted).IsEmpty();
    }

    [Test]
    public async Task A_main_flag_of_false_is_posted_on_the_start_and_on_its_replay() {
        Grant();
        var dir    = new WalkBuilder(Walks) { IsMain = false, FetchedAtMs = T0 + Minute }.AddPage(null, null, false, User(1, T0)).Write("side-chat");
        var failed = new RecordingPoster { Refuse = (_, i) => i == 0 ? "hooks/session-start/muse-assistant answered 503" : null };

        await Assert.That(await (await IngestAsync(failed)).RunAsync(dir, default)).IsEqualTo(1);
        await Assert.That(Store().LoadState("chat-main").State!.Open!.IsMain).IsFalse();

        var replayed = new RecordingPoster();
        await Assert.That(await (await IngestAsync(replayed)).RunAsync(dir, default)).IsEqualTo(0);

        await Assert.That(replayed.Accepted.OfType<MuseAssistantStartSession>().Single().Session.IsMain).IsFalse();
    }

    [Test]
    public async Task A_profile_switch_hides_the_other_profiles_state_and_posts_nothing() {
        Grant();
        var dir = TwoSessionWalk();
        var other = new RecordingPoster();

        await Assert.That(await (await IngestAsync(other, userId: "user_01OTHER")).RunAsync(dir, default)).IsEqualTo(3);
        await Assert.That(other.Accepted).IsEmpty();
        await Assert.That(Directory.Exists(dir)).IsTrue();

        var mine = new RecordingPoster();
        await Assert.That(await (await IngestAsync(mine)).RunAsync(dir, default)).IsEqualTo(0);
        await Assert.That(mine.Starts).IsEquivalentTo([Id(1), Id(5)], CollectionOrdering.Matching);
    }

    [Test]
    public async Task A_mid_batch_500_leaves_the_watermark_at_the_last_accepted_batch_and_a_rerun_resumes() {
        Grant();
        var items  = Enumerable.Range(1, 105).Select(i => User(i, T0 + i)).ToArray();
        var dir    = new WalkBuilder(Walks) { FetchedAtMs = T0 + Minute }.AddPage(null, null, false, items).Write();
        var poster = new RecordingPoster { Refuse = (_, i) => i == 2 ? "hooks/transcript answered 500" : null };

        await Assert.That(await (await IngestAsync(poster)).RunAsync(dir, default)).IsEqualTo(1);

        var state = Store().LoadState("chat-main").State!;
        await Assert.That(state.Watermark).IsEqualTo(100);
        await Assert.That(state.Open!.Phase).IsEqualTo(MuseAssistantSessionPhases.Started);

        var rerun = new RecordingPoster();
        await Assert.That(await (await IngestAsync(rerun)).RunAsync(dir, default)).IsEqualTo(0);
        await Assert.That(rerun.Kinds).IsEquivalentTo([nameof(MuseAssistantSendLines)]);
        await Assert.That(rerun.Seqs).IsEquivalentTo([101, 102, 103, 104, 105], CollectionOrdering.Matching);
    }

    [Test]
    [NotInParallel]
    public async Task A_401_exits_1_names_kcap_login_and_leaves_the_watermark_unchanged() {
        Grant();
        using var stderr = ConsoleOutput.StartErrorCapture();
        var poster = new RecordingPoster { Refuse = (_, _) => "the server refused the sign-in (401); run `kcap login --device`" };

        await Assert.That(await (await IngestAsync(poster)).RunAsync(TwoSessionWalk(), default)).IsEqualTo(1);

        await Assert.That(stderr.GetCapturedError()).Contains("kcap login");
        var state = Store().LoadState("chat-main").State!;
        await Assert.That(state.Watermark).IsNull();
        await Assert.That(state.Open!.Phase).IsEqualTo(MuseAssistantSessionPhases.PendingStart);   // the intent is recorded; the rerun replays it
    }

    [Test]
    public async Task Lock_contention_exits_0_without_posting_and_a_released_lock_runs() {
        Grant();
        var poster = new RecordingPoster();
        var ingest = await IngestAsync(poster);
        var dir    = TwoSessionWalk();

        using (MuseAssistantChatLock.TryAcquire(MuseAssistantPaths.ChatLock(Store().ScopeDir, "chat-main"))) {
            await Assert.That(await ingest.RunAsync(dir, default)).IsEqualTo(0);
            await Assert.That(poster.Accepted).IsEmpty();
        }

        await Assert.That(await ingest.RunAsync(dir, default)).IsEqualTo(0);
        await Assert.That(poster.Accepted).IsNotEmpty();
    }

    [Test]
    public async Task Ingests_of_different_chats_do_not_block_each_other() {
        Grant("chat-main");
        Grant("chat-two");
        var poster = new RecordingPoster();
        var dir    = new WalkBuilder(Walks) { ChatId = "chat-two" }.AddPage(null, null, false, User(1, T0)).Write();

        using var held = MuseAssistantChatLock.TryAcquire(MuseAssistantPaths.ChatLock(Store().ScopeDir, "chat-main"));
        await Assert.That(held).IsNotNull();
        await Assert.That(await (await IngestAsync(poster)).RunAsync(dir, default)).IsEqualTo(0);
        await Assert.That(poster.Seqs).IsEquivalentTo([1]);
    }

    [Test]
    public async Task A_crash_after_any_accepted_post_then_a_rerun_yields_the_same_sessions_and_loses_nothing() {
        Grant();
        var baseline = new RecordingPoster();
        await (await IngestAsync(baseline)).RunAsync(TwoSessionWalk("base"), default);
        var expectedStarts = baseline.Starts.ToList();
        var expectedEnds   = baseline.Ends.ToList();
        var expectedSeqs   = baseline.Seqs.ToList();
        await Assert.That(baseline.Accepted.Count).IsEqualTo(6);

        for (var k = 0; k < baseline.Accepted.Count; k++) {
            ResetState();
            Grant();
            var crashing = new RecordingPoster { CrashAfterAccepting = k };
            await Assert.That(async () => await (await IngestAsync(crashing)).RunAsync(TwoSessionWalk($"crash{k}"), default)).Throws<SimulatedCrash>();

            var rerun = new RecordingPoster();
            await Assert.That(await (await IngestAsync(rerun)).RunAsync(TwoSessionWalk($"rerun{k}"), default)).IsEqualTo(0);

            var all = crashing.Accepted.Concat(rerun.Accepted).ToList();
            await Assert.That(all.OfType<MuseAssistantStartSession>().Select(a => a.Session.SessionId).Distinct()).IsEquivalentTo(expectedStarts).Because($"crash after post {k}");
            await Assert.That(all.OfType<MuseAssistantEndSession>().Select(a => a.Session.SessionId).Distinct()).IsEquivalentTo(expectedEnds).Because($"crash after post {k}");
            await Assert.That(all.OfType<MuseAssistantSendLines>().SelectMany(a => a.Seqs).Distinct().Order()).IsEquivalentTo(expectedSeqs).Because($"crash after post {k}");

            // The only repeat allowed is the call whose result never reached the state file.
            var repeated = all.GroupBy(Key).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            await Assert.That(repeated.Count).IsLessThanOrEqualTo(1).Because($"crash after post {k}: {string.Join(", ", repeated)}");
            if (repeated.Count == 1) await Assert.That(repeated[0]).IsEqualTo(Key(crashing.Accepted[k]));
        }
    }

    [Test]
    public async Task A_crash_before_any_state_write_then_a_rerun_converges_the_same_way() {
        Grant();
        var baseline = new RecordingPoster();
        await (await IngestAsync(baseline)).RunAsync(TwoSessionWalk("base"), default);
        var expectedSeqs = baseline.Seqs.ToList();

        for (var n = 0; n < 12; n++) {
            ResetState();
            Grant();
            var crashing = new RecordingPoster();
            var attempt  = await IngestAsync(crashing, new CrashingStoreFactory(n));
            try { await attempt.RunAsync(TwoSessionWalk($"w{n}"), default); } catch (SimulatedCrash) { }

            var rerun = new RecordingPoster();
            await Assert.That(await (await IngestAsync(rerun)).RunAsync(TwoSessionWalk($"r{n}"), default)).IsEqualTo(0);

            var all = crashing.Accepted.Concat(rerun.Accepted).ToList();
            await Assert.That(all.OfType<MuseAssistantStartSession>().Select(a => a.Session.SessionId).Distinct()).IsEquivalentTo([Id(1), Id(5)]).Because($"crash on write {n}");
            await Assert.That(all.OfType<MuseAssistantSendLines>().SelectMany(a => a.Seqs).Distinct().Order()).IsEquivalentTo(expectedSeqs).Because($"crash on write {n}");
            await Assert.That(all.OfType<MuseAssistantEndSession>().Select(a => a.Session.SessionId).Distinct()).IsEquivalentTo([Id(1), Id(5)]).Because($"crash on write {n}");

            // The only repeat allowed is the last call the crash accepted: its result never reached the state file.
            var repeated = all.GroupBy(Key).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            await Assert.That(repeated.Count).IsLessThanOrEqualTo(1).Because($"crash on write {n}: {string.Join(", ", repeated)}");
            if (repeated.Count == 1) await Assert.That(repeated[0]).IsEqualTo(Key(crashing.Accepted.Last())).Because($"crash on write {n}");
        }
    }

    [Test]
    public async Task A_retained_directory_after_a_server_failure_completes_on_retry_and_a_shorter_fresh_walk_also_validates() {
        Grant();
        var items = Enumerable.Range(1, 105).Select(i => User(i, T0 + i)).ToArray();
        var kept  = new WalkBuilder(Walks) { FetchedAtMs = T0 + Minute }.AddPage(null, null, false, items).Write("kept");
        var first = new RecordingPoster { Refuse = (_, i) => i == 2 ? "hooks/transcript answered 503" : null };
        await Assert.That(await (await IngestAsync(first)).RunAsync(kept, default)).IsEqualTo(1);

        var retry = new RecordingPoster();
        await Assert.That(await (await IngestAsync(retry)).RunAsync(kept, default)).IsEqualTo(0);
        await Assert.That(retry.Seqs).IsEquivalentTo([101, 102, 103, 104, 105], CollectionOrdering.Matching);

        // The next tick's walk stops at the first page that overlaps the watermark.
        var fresh = new WalkBuilder(Walks) { FetchedAtMs = T0 + 2 * Minute }
            .AddPage(null, "c1", true, User(100, T0 + 100), User(106, T0 + 106), User(107, T0 + 107)).Write("fresh");
        var next = new RecordingPoster();
        await Assert.That(await (await IngestAsync(next)).RunAsync(fresh, default)).IsEqualTo(0);
        await Assert.That(next.Seqs).IsEquivalentTo([106, 107], CollectionOrdering.Matching);

        var sent = first.Seqs.Concat(retry.Seqs).Concat(next.Seqs).ToList();
        await Assert.That(sent.Distinct().Count()).IsEqualTo(sent.Count);
    }

    [Test]
    public async Task Idle_closure_happens_only_in_the_ingest_and_only_against_the_fetch_time() {
        Grant();
        var ten = T0;
        var firstWalk = new WalkBuilder(Walks) { FetchedAtMs = ten + 5 * Minute }.AddPage(null, null, false, User(1, ten)).Write("ten-oh-five");
        await (await IngestAsync(new RecordingPoster())).RunAsync(firstWalk, default);

        // 12:15 cron, a new item at 11:59: one session, no end.
        var continued = new RecordingPoster();
        var w1215 = new WalkBuilder(Walks) { FetchedAtMs = ten + 135 * Minute }.AddPage(null, null, false, User(1, ten), User(2, ten + 119 * Minute)).Write("twelve-fifteen");
        await Assert.That(await (await IngestAsync(continued)).RunAsync(w1215, default)).IsEqualTo(0);
        await Assert.That(continued.Kinds).IsEquivalentTo([nameof(MuseAssistantSendLines)]);
        await Assert.That(Store().LoadState("chat-main").State!.Open).IsNotNull();

        // A retained 10:05 walk retried later can never close on the strength of what Muse had at 10:05.
        var retried = new RecordingPoster();
        await Assert.That(await (await IngestAsync(retried)).RunAsync(firstWalk, default)).IsEqualTo(0);
        await Assert.That(retried.Accepted).IsEmpty();

        // Nothing new by 14:30 (2 h 31 m after 11:59): page 000 alone closes the session.
        var closing = new RecordingPoster();
        var w1430 = new WalkBuilder(Walks) { FetchedAtMs = ten + 119 * Minute + 151 * Minute }.AddPage(null, null, false, User(1, ten), User(2, ten + 119 * Minute)).Write("fourteen-thirty");
        await Assert.That(await (await IngestAsync(closing)).RunAsync(w1430, default)).IsEqualTo(0);
        await Assert.That(closing.Kinds).IsEquivalentTo([nameof(MuseAssistantEndSession)]);
        await Assert.That(((MuseAssistantEndSession)closing.Accepted[0]).EndedAtMs).IsEqualTo(ten + 119 * Minute);
    }

    /// <summary>A 10:05 walk kept after a refused start or a refused batch, retried at 12:15, still judges
    /// idleness at 10:05: it records the 10:00 item and closes nothing, and the fresh 12:15 walk continues
    /// the same session with the 11:59 item.</summary>
    [Test]
    [Arguments(0)]
    [Arguments(1)]
    public async Task A_walk_kept_after_a_failed_post_and_retried_later_never_closes_and_the_fresh_walk_continues(int refusedCall) {
        Grant();
        var ten    = T0;
        var kept   = new WalkBuilder(Walks) { FetchedAtMs = ten + 5 * Minute }.AddPage(null, null, false, User(1, ten)).Write("ten-oh-five");
        var failed = new RecordingPoster { Refuse = (_, i) => i == refusedCall ? "hooks/transcript answered 503" : null };
        await Assert.That(await (await IngestAsync(failed)).RunAsync(kept, default)).IsEqualTo(1);

        var retried = new RecordingPoster();
        await Assert.That(await (await IngestAsync(retried)).RunAsync(kept, default)).IsEqualTo(0);
        await Assert.That(retried.Ends).IsEmpty();
        await Assert.That(retried.Seqs).IsEquivalentTo([1]);

        var fresh = new WalkBuilder(Walks) { FetchedAtMs = ten + 135 * Minute }.AddPage(null, null, false, User(1, ten), User(2, ten + 119 * Minute)).Write("twelve-fifteen");
        var next  = new RecordingPoster();
        await Assert.That(await (await IngestAsync(next)).RunAsync(fresh, default)).IsEqualTo(0);
        await Assert.That(next.Kinds).IsEquivalentTo([nameof(MuseAssistantSendLines)]);
        await Assert.That(next.Seqs).IsEquivalentTo([2]);

        var all = failed.Accepted.Concat(retried.Accepted).Concat(next.Accepted).ToList();
        await Assert.That(all.OfType<MuseAssistantStartSession>().Select(a => a.Session.SessionId)).IsEquivalentTo([Id(1)]);
        await Assert.That(Store().LoadState("chat-main").State!.Open!.SessionId).IsEqualTo(Id(1));
    }

    static string Key(MuseAssistantAction a) => a switch {
        MuseAssistantStartSession s => "start:" + s.Session.SessionId,
        MuseAssistantEndSession e   => "end:" + e.Session.SessionId,
        MuseAssistantSendLines l    => "send:" + l.SessionId + ":" + string.Join(",", l.Seqs),
        _                           => a.GetType().Name
    };
}
