using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// Pins the title resolution ladder: native transcript title first, the server's real title
/// as the authority once it exists, local generation only as the late fallback. The native
/// title is pushed to the server whenever it changes, independent of the server's own title;
/// a locally generated title converges only while the server verifiably has no title and no
/// native title exists. A server title that merely echoes the launch prompt (the watcher's
/// initial truncated-prompt title) counts as "no real title yet".
/// </summary>
public class TitleResolveLoopTests {
    [TempDir] public required TempDir Tmp { get; init; }

    static readonly DateTime T0 = new(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);

    /// By default behaves like the real server: a successfully pushed native title is what the
    /// next GET returns. Assign <see cref="Get"/> to script the read side explicitly.
    sealed class FakeServerPort : ITitleServerPort {
        public Func<string, string?>? Get { get; set; }
        public string? Committed { get; private set; }
        public List<(string SessionId, HarnessTitlePost Post)> Pushed { get; } = [];
        public HarnessTitleOutcome PushResult { get; set; } = HarnessTitleOutcome.Posted;

        public Task<string?> GetTitleAsync(string sessionId, CancellationToken ct) =>
            Task.FromResult(Get is not null ? Get(sessionId) : Committed);

        public Task<HarnessTitleOutcome> PushTitleAsync(string sessionId, HarnessTitlePost post, CancellationToken ct) {
            Pushed.Add((sessionId, post));
            if (PushResult == HarnessTitleOutcome.Posted) Committed = post.Title;
            return Task.FromResult(PushResult);
        }
    }

    sealed class Harness {
        public List<TitleAgentView> Agents { get; } = [];
        public List<(string AgentId, string Title)> Applied { get; } = [];
        public FakeServerPort Server { get; } = new();
        public Func<TitleAgentView, string?> Native { get; set; } = _ => null;
        public HarnessTitleKind NativeKind { get; set; } = HarnessTitleKind.Rename;
        public DateTimeOffset? NativeChangedAt { get; set; }
        public int NativeCalls;
        public Func<TitleAgentView, CancellationToken, Task<string?>> Generate { get; set; } =
            (_, _) => Task.FromResult<string?>(null);
        public int GenerateCalls;
        public List<(string SessionId, string Title)> GeneratedPosts { get; } = [];
        public bool PostGeneratedResult { get; set; } = true;
        public FakeTimeProvider Time { get; } = new(T0);

        public TitleResolveLoop Build() => new(
            () => Agents,
            (id, title) => Applied.Add((id, title)),
            Server,
            a => {
                Interlocked.Increment(ref NativeCalls);
                return Native(a) is { } title ? new HarnessTitlePost(title, NativeKind, NativeChangedAt) : null;
            },
            (a, ct) => { Interlocked.Increment(ref GenerateCalls); return Generate(a, ct); },
            (sessionId, title, ct) => {
                GeneratedPosts.Add((sessionId, title));
                return Task.FromResult(PostGeneratedResult);
            },
            Time,
            NullLogger.Instance);
    }

    static TitleAgentView Agent(
            string id = "a1", string vendor = "claude", string? prompt = "Fix the login bug in the auth flow",
            string? sessionId = "sid-1", string? transcript = "/t.jsonl", DateTime? createdAt = null) =>
        new(id, vendor, prompt, sessionId, transcript, createdAt ?? T0);

    [Test]
    public async Task A_new_native_rename_is_pushed_even_when_the_server_has_a_title() {
        var h = new Harness();
        h.Agents.Add(Agent());
        var changedAt = new DateTimeOffset(2026, 9, 1, 9, 0, 0, TimeSpan.Zero);
        h.Server.Get = _ => "R";
        h.Native = _ => "B";
        h.NativeChangedAt = changedAt;
        var loop = h.Build();

        await loop.TickAsync(CancellationToken.None);

        await Assert.That(h.Applied.First().Title).IsEqualTo("R");
        await Assert.That(h.Server.Pushed).IsEquivalentTo([("sid-1", new HarnessTitlePost("B", HarnessTitleKind.Rename, changedAt))]);

        h.Server.Get = _ => "B"; // the server now shows the pushed rename
        await loop.TickAsync(CancellationToken.None);

        await Assert.That(h.Applied.Last().Title).IsEqualTo("B");
    }

    [Test]
    public async Task A_revised_native_title_is_re_applied() {
        var h = new Harness();
        h.Agents.Add(Agent());
        var native = "First cut";
        h.Native = _ => native;
        var loop = h.Build();

        await loop.TickAsync(CancellationToken.None);
        native = "Second cut";
        await loop.TickAsync(CancellationToken.None);

        await Assert.That(h.Applied.Select(a => a.Title)).IsEquivalentTo(["First cut", "Second cut"]);
    }

    [Test]
    public async Task An_unchanged_native_value_is_never_pushed_twice() {
        var h = new Harness();
        h.Agents.Add(Agent());
        h.Native = _ => "A";
        var loop = h.Build();

        await loop.TickAsync(CancellationToken.None); // pushes "A" once
        h.Server.Get = _ => "R";

        for (var i = 0; i < 10; i++) await loop.TickAsync(CancellationToken.None);

        await Assert.That(h.Server.Pushed.Count).IsEqualTo(1);
        await Assert.That(h.Applied.Last().Title).IsEqualTo("R");
    }

    [Test]
    public async Task A_server_title_echoing_the_prompt_is_not_adopted_and_does_not_block_the_push() {
        var h = new Harness();
        h.Agents.Add(Agent(prompt: "Fix the login bug in the auth flow, then add tests"));
        h.Native = _ => "Native title";
        h.Server.Get = _ => "Fix the login bug in the auth flow, then...";
        var loop = h.Build();

        await loop.TickAsync(CancellationToken.None);

        await Assert.That(h.Applied).IsEquivalentTo([("a1", "Native title")]);
        await Assert.That(h.Server.Pushed).IsEquivalentTo([("sid-1", new HarnessTitlePost("Native title", HarnessTitleKind.Rename, null))]);
    }

    [Test]
    public async Task An_unrecorded_agent_generates_once_after_the_grace_period() {
        var h = new Harness();
        h.Agents.Add(Agent(sessionId: null));
        h.Generate = (_, _) => Task.FromResult<string?>("Generated title");
        var loop = h.Build();

        await loop.TickAsync(CancellationToken.None);
        await Assert.That(h.Applied).IsEmpty(); // still inside the grace period

        h.Time.Advance(TimeSpan.FromMinutes(6));
        await loop.TickAsync(CancellationToken.None);
        await loop.TickAsync(CancellationToken.None);

        await Assert.That(h.Applied).IsEquivalentTo([("a1", "Generated title")]);
        await Assert.That(h.GenerateCalls).IsEqualTo(1);
        await Assert.That(h.Server.Pushed).IsEmpty(); // no session to converge to
    }

    [Test]
    public async Task A_recorded_agent_generates_and_pushes_when_the_server_stays_silent() {
        var h = new Harness();
        h.Agents.Add(Agent());
        h.Generate = (_, _) => Task.FromResult<string?>("Generated title");
        h.Time.Advance(TimeSpan.FromMinutes(6));
        var loop = h.Build();

        await loop.TickAsync(CancellationToken.None);

        await Assert.That(h.Applied).IsEquivalentTo([("a1", "Generated title")]);
        await Assert.That(h.GeneratedPosts).IsEquivalentTo([("sid-1", "Generated title")]);
        await Assert.That(h.Server.Pushed).IsEmpty();
    }

    [Test]
    public async Task A_recorded_agent_with_a_real_server_title_never_generates() {
        var h = new Harness();
        h.Agents.Add(Agent());
        h.Server.Get = _ => "Server generated title";
        h.Time.Advance(TimeSpan.FromMinutes(30));
        var loop = h.Build();

        await loop.TickAsync(CancellationToken.None);
        await loop.TickAsync(CancellationToken.None);

        await Assert.That(h.GenerateCalls).IsEqualTo(0);
        await Assert.That(h.Applied).IsEquivalentTo([("a1", "Server generated title")]);
    }

    [Test]
    public async Task An_agent_with_a_native_title_never_generates() {
        var h = new Harness();
        h.Agents.Add(Agent());
        h.Native = _ => "Native title";
        h.Time.Advance(TimeSpan.FromMinutes(30));
        var loop = h.Build();

        await loop.TickAsync(CancellationToken.None);

        await Assert.That(h.GenerateCalls).IsEqualTo(0);
    }

    [Test]
    public async Task A_recorded_agent_does_not_generate_when_the_server_cannot_be_read() {
        var h = new Harness();
        h.Agents.Add(Agent());
        h.Server.Get = _ => throw new HttpRequestException("outage");
        h.Generate = (_, _) => Task.FromResult<string?>("Generated title");
        h.Time.Advance(TimeSpan.FromMinutes(30));
        var loop = h.Build();

        await loop.TickAsync(CancellationToken.None);

        await Assert.That(h.GenerateCalls).IsEqualTo(0);
    }

    [Test]
    public async Task A_failed_server_read_still_pushes_a_native_title() {
        var h = new Harness();
        h.Agents.Add(Agent());
        h.Native = _ => "Native title";
        h.Server.Get = _ => throw new HttpRequestException("outage");
        var loop = h.Build();

        await loop.TickAsync(CancellationToken.None);

        // The native push is unconditional on the harness's own title changing — a failed
        // read of the server's title does not gate it, unlike the generated fallback's push.
        await Assert.That(h.Applied).IsEquivalentTo([("a1", "Native title")]);
        await Assert.That(h.Server.Pushed).IsEquivalentTo([("sid-1", new HarnessTitlePost("Native title", HarnessTitleKind.Rename, null))]);
    }

    [Test]
    public async Task The_loops_own_pushed_title_does_not_block_a_native_revision() {
        var h = new Harness();
        h.Agents.Add(Agent());
        var native = "First cut";
        h.Native = _ => native;
        var loop = h.Build();

        await loop.TickAsync(CancellationToken.None);
        h.Server.Get = _ => "First cut"; // the server now echoes what the loop itself pushed
        native = "Second cut";
        await loop.TickAsync(CancellationToken.None);

        await Assert.That(h.Applied.Select(a => a.Title)).IsEquivalentTo(["First cut", "Second cut"]);
        await Assert.That(h.Server.Pushed.Select(p => p.Post.Title)).IsEquivalentTo(["First cut", "Second cut"]);
    }

    [Test]
    public async Task Display_prefers_the_server_title_then_native_then_generated() {
        var h = new Harness();
        h.Agents.Add(Agent());
        h.Generate = (_, _) => Task.FromResult<string?>("Generated title");
        h.Time.Advance(TimeSpan.FromMinutes(6));
        var loop = h.Build();

        await loop.TickAsync(CancellationToken.None);
        await Assert.That(h.Applied.Last().Title).IsEqualTo("Generated title");

        h.Native = _ => "Native title";
        await loop.TickAsync(CancellationToken.None);
        await Assert.That(h.Applied.Last().Title).IsEqualTo("Native title");

        h.Server.Get = _ => "Server title"; // an independent title, not our own push
        await loop.TickAsync(CancellationToken.None);
        await Assert.That(h.Applied.Last().Title).IsEqualTo("Server title");
    }

    [Test]
    public async Task A_short_real_title_that_prefixes_the_prompt_is_still_adopted() {
        var h = new Harness();
        h.Agents.Add(Agent(prompt: "Fix login timeout by adding retries"));
        h.Server.Get = _ => "Fix login timeout"; // a genuine title, not a truncation
        var loop = h.Build();

        await loop.TickAsync(CancellationToken.None);

        await Assert.That(h.Applied).IsEquivalentTo([("a1", "Fix login timeout")]);
    }

    [Test]
    public async Task A_full_first_line_echo_is_still_not_adopted() {
        var h = new Harness();
        h.Agents.Add(Agent(prompt: "Fix login timeout by adding retries\nmore detail"));
        h.Server.Get = _ => "Fix login timeout by adding retries";
        var loop = h.Build();

        await loop.TickAsync(CancellationToken.None);

        await Assert.That(h.Applied).IsEmpty();
    }

    [Test]
    public async Task An_adopted_server_title_survives_a_failed_read() {
        var h = new Harness();
        h.Agents.Add(Agent());
        h.Server.Get = _ => "Server generated title";
        var loop = h.Build();

        await loop.TickAsync(CancellationToken.None);
        h.Server.Get = _ => throw new HttpRequestException("outage");
        h.Native = _ => "Native title";
        await loop.TickAsync(CancellationToken.None);

        // The outage tick must not demote the adopted server title to the native one.
        await Assert.That(h.Applied).IsEquivalentTo([("a1", "Server generated title")]);

        // Only a successful read proving the server silent releases the authority.
        h.Server.Get = _ => null;
        await loop.TickAsync(CancellationToken.None);
        await Assert.That(h.Applied.Last().Title).IsEqualTo("Native title");
    }

    [Test]
    public async Task An_unconfirmed_push_does_not_become_server_authority() {
        var h = new Harness();
        h.Agents.Add(Agent());
        var native = "First cut";
        h.Native = _ => native;
        h.Server.PushResult = HarnessTitleOutcome.Failed; // the push may have committed server-side; only its ack is lost
        var loop = h.Build();

        await loop.TickAsync(CancellationToken.None);
        h.Server.Get = _ => "First cut"; // the committed-but-unacknowledged push coming back
        h.Server.PushResult = HarnessTitleOutcome.Posted;
        native = "Second cut";
        await loop.TickAsync(CancellationToken.None);

        await Assert.That(h.Applied.Select(a => a.Title)).IsEquivalentTo(["First cut", "Second cut"]);
        await Assert.That(h.Server.Pushed.Last().Post.Title).IsEqualTo("Second cut");
    }

    [Test]
    public async Task A_native_push_survives_an_independent_title_landing_and_leaving() {
        var h = new Harness();
        h.Agents.Add(Agent());
        h.Native = _ => "Native title";
        var loop = h.Build();

        await loop.TickAsync(CancellationToken.None); // pushes Native title once
        h.Server.Get = _ => "Watcher generated title";
        await loop.TickAsync(CancellationToken.None); // independent authority adopted for display
        h.Server.Get = _ => null; // the server's title is later confirmed gone
        await loop.TickAsync(CancellationToken.None);

        // The unchanged native value is never re-pushed, regardless of what the server showed
        // in between.
        await Assert.That(h.Applied.Last().Title).IsEqualTo("Native title");
        await Assert.That(h.Server.Pushed.Select(p => p.Post.Title)).IsEquivalentTo(["Native title"]);
    }

    [Test]
    public async Task A_delayed_echo_of_an_older_attempt_is_not_authority() {
        var h = new Harness();
        h.Agents.Add(Agent());
        var native = "First cut";
        h.Native = _ => native;
        h.Server.PushResult = HarnessTitleOutcome.Failed; // acks lost; either attempt may have committed
        h.Server.Get = _ => null;
        var loop = h.Build();

        await loop.TickAsync(CancellationToken.None); // attempts First cut
        native = "Second cut";
        await loop.TickAsync(CancellationToken.None); // attempts Second cut
        h.Server.Get = _ => "First cut"; // the older attempt surfaces late
        await loop.TickAsync(CancellationToken.None);

        // The superseded attempt is still ours — it must not freeze the ladder as authority.
        await Assert.That(h.Applied.Select(a => a.Title)).IsEquivalentTo(["First cut", "Second cut"]);
    }

    [Test]
    public async Task Attempt_history_evicts_oldest_only_at_capacity() {
        var h = new Harness();
        h.Agents.Add(Agent());
        var native = "Cut 01";
        h.Native = _ => native;
        h.Server.PushResult = HarnessTitleOutcome.Failed;
        h.Server.Get = _ => null;
        var loop = h.Build();

        for (var i = 1; i <= 33; i++) {
            native = $"Cut {i:D2}";
            await loop.TickAsync(CancellationToken.None);
        }
        await loop.TickAsync(CancellationToken.None); // duplicate retry of Cut 33 must not evict

        // Cut 02 is within the retained window — still ours, never authority.
        h.Server.Get = _ => "Cut 02";
        await loop.TickAsync(CancellationToken.None);
        await Assert.That(h.Applied.Last().Title).IsEqualTo("Cut 33");

        // Cut 01 fell off the 32-entry window — the documented boundary where an echo can win.
        h.Server.Get = _ => "Cut 01";
        await loop.TickAsync(CancellationToken.None);
        await Assert.That(h.Applied.Last().Title).IsEqualTo("Cut 01");
    }

    [Test]
    public async Task A_transient_native_gap_does_not_restore_the_generated_fallback() {
        var h = new Harness();
        h.Agents.Add(Agent());
        h.Generate = (_, _) => Task.FromResult<string?>("Generated title");
        h.Time.Advance(TimeSpan.FromMinutes(6));
        var loop = h.Build();

        await loop.TickAsync(CancellationToken.None);
        h.Native = _ => "Native title";
        await loop.TickAsync(CancellationToken.None);
        h.Native = _ => null;
        await loop.TickAsync(CancellationToken.None);

        await Assert.That(h.Applied.Select(a => a.Title)).IsEquivalentTo(["Generated title", "Native title"]);
        await Assert.That(h.GeneratedPosts.Select(p => p.Title)).IsEquivalentTo(["Generated title"]);
        await Assert.That(h.Server.Pushed.Select(p => p.Post.Title)).IsEquivalentTo(["Native title"]);
    }

    [Test]
    public async Task A_title_landing_during_generation_wins_over_the_generated_one() {
        var h = new Harness();
        h.Agents.Add(Agent());
        h.Generate = (_, _) => {
            // An independent watcher title lands while the model call runs.
            h.Server.Get = _ => "Watcher generated title";
            return Task.FromResult<string?>("Generated title");
        };
        h.Time.Advance(TimeSpan.FromMinutes(6));
        var loop = h.Build();

        await loop.TickAsync(CancellationToken.None);

        await Assert.That(h.Applied).IsEquivalentTo([("a1", "Watcher generated title")]);
        await Assert.That(h.GeneratedPosts).IsEmpty();
        await Assert.That(h.Server.Pushed).IsEmpty();
    }

    [Test]
    public async Task A_failed_push_is_retried_on_the_next_tick() {
        var h = new Harness();
        h.Agents.Add(Agent());
        h.Native = _ => "Native title";
        h.Server.PushResult = HarnessTitleOutcome.Failed;
        var loop = h.Build();

        await loop.TickAsync(CancellationToken.None);
        h.Server.PushResult = HarnessTitleOutcome.Posted;
        await loop.TickAsync(CancellationToken.None);
        await loop.TickAsync(CancellationToken.None);

        await Assert.That(h.Server.Pushed.Count).IsEqualTo(2);
    }

    [Test]
    public async Task A_refused_push_is_not_retried_for_the_same_value() {
        var h = new Harness();
        h.Agents.Add(Agent());
        h.Native = _ => "Native title";
        h.Server.PushResult = HarnessTitleOutcome.Refused; // e.g. blank title, unsafe id, not the owner
        var loop = h.Build();

        await loop.TickAsync(CancellationToken.None);
        await loop.TickAsync(CancellationToken.None);
        await loop.TickAsync(CancellationToken.None);

        // A verdict on this value, not a transient hiccup — retrying forever would spend a
        // request for nothing.
        await Assert.That(h.Server.Pushed.Count).IsEqualTo(1);
    }

    [Test]
    public async Task A_session_not_found_push_is_retried_on_the_next_tick() {
        var h = new Harness();
        h.Agents.Add(Agent());
        h.Native = _ => "Native title";
        h.Server.PushResult = HarnessTitleOutcome.SessionNotFound; // not yet projected server-side
        var loop = h.Build();

        await loop.TickAsync(CancellationToken.None);
        await loop.TickAsync(CancellationToken.None);

        await Assert.That(h.Server.Pushed.Count).IsEqualTo(2);
    }

    [Test]
    public async Task A_failed_generated_push_is_retried_on_the_next_tick() {
        var h = new Harness();
        h.Agents.Add(Agent());
        h.Generate = (_, _) => Task.FromResult<string?>("Generated title");
        h.Time.Advance(TimeSpan.FromMinutes(6));
        h.PostGeneratedResult = false;
        var loop = h.Build();

        await loop.TickAsync(CancellationToken.None);
        h.PostGeneratedResult = true;
        await loop.TickAsync(CancellationToken.None);
        await loop.TickAsync(CancellationToken.None);

        await Assert.That(h.GeneratedPosts.Count).IsEqualTo(2);
    }

    [Test]
    public async Task A_throwing_lane_does_not_break_the_tick_for_other_agents() {
        var h = new Harness();
        h.Agents.Add(Agent(id: "bad", transcript: "/bad.jsonl"));
        h.Agents.Add(Agent(id: "good", sessionId: "sid-good"));
        h.Native = a => a.Id == "bad" ? throw new IOException("boom") : "Good title";
        var loop = h.Build();

        await loop.TickAsync(CancellationToken.None);

        await Assert.That(h.Applied).IsEquivalentTo([("good", "Good title")]);
    }

    [Test]
    public async Task State_for_departed_agents_is_dropped() {
        var h = new Harness();
        var agent = Agent();
        h.Agents.Add(agent);
        h.Native = _ => "Native title";
        var loop = h.Build();

        await loop.TickAsync(CancellationToken.None);
        h.Agents.Clear();
        await loop.TickAsync(CancellationToken.None);
        h.Agents.Add(agent);
        await loop.TickAsync(CancellationToken.None);

        // Fresh state after re-appearance: the title is applied (and pushed) again.
        await Assert.That(h.Applied.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Native_extraction_is_skipped_when_the_transcript_file_is_unchanged() {
        var path = Tmp.CreateFile("t.jsonl", "irrelevant — the native lane is faked below");
        var writeTime = File.GetLastWriteTimeUtc(path);

        var h = new Harness();
        h.Agents.Add(Agent(transcript: path));
        h.Native = _ => "Native title";
        var loop = h.Build();

        await loop.TickAsync(CancellationToken.None);
        await loop.TickAsync(CancellationToken.None);
        await loop.TickAsync(CancellationToken.None);

        await Assert.That(h.NativeCalls).IsEqualTo(1);

        File.SetLastWriteTimeUtc(path, writeTime + TimeSpan.FromSeconds(1));
        await loop.TickAsync(CancellationToken.None);

        await Assert.That(h.NativeCalls).IsEqualTo(2);
    }

    [Test]
    public async Task A_departed_agents_native_extraction_cache_is_dropped_with_its_state() {
        var path = Tmp.CreateFile("t.jsonl", "irrelevant — the native lane is faked below");

        var h = new Harness();
        var agent = Agent(transcript: path);
        h.Agents.Add(agent);
        h.Native = _ => "Native title";
        var loop = h.Build();

        await loop.TickAsync(CancellationToken.None);
        h.Agents.Clear();
        await loop.TickAsync(CancellationToken.None); // agent gone: no extraction, cache untouched
        h.Agents.Add(agent);
        await loop.TickAsync(CancellationToken.None); // fresh state: re-extracts despite the unchanged file

        await Assert.That(h.NativeCalls).IsEqualTo(2);
    }

    /// <summary>The push carries the title uncapped, as the watcher sends the same rename, while only the local
    /// display is capped; the server echoing the whole title back is still recognised as this loop's own.</summary>
    [Test]
    public async Task A_long_native_title_is_pushed_whole_and_applied_capped() {
        var h         = new Harness();
        var longTitle = new string('n', 150);
        h.Agents.Add(Agent());
        h.Native = _ => longTitle;
        var loop = h.Build();

        await loop.TickAsync(CancellationToken.None);
        await loop.TickAsync(CancellationToken.None);

        await Assert.That(h.Server.Pushed.Select(p => p.Post.Title)).IsEquivalentTo([longTitle]);
        await Assert.That(h.Applied).IsEquivalentTo([("a1", new string('n', 120))]);
    }

    /// <summary>An auth lapse reaches the loop as <see cref="HarnessTitleOutcome.Failed"/>, which stays retryable.</summary>
    [Test]
    public async Task A_failed_native_push_is_retried_on_the_next_tick() {
        var h = new Harness();
        h.Agents.Add(Agent());
        h.Native = _ => "A";
        h.Server.PushResult = HarnessTitleOutcome.Failed;
        var loop = h.Build();

        await loop.TickAsync(CancellationToken.None);
        h.Server.PushResult = HarnessTitleOutcome.Posted;
        await loop.TickAsync(CancellationToken.None);
        await loop.TickAsync(CancellationToken.None);

        await Assert.That(h.Server.Pushed.Count).IsEqualTo(2);
    }
}
