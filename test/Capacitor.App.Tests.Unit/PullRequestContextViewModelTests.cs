using System.Reactive.Linq;
using System.Reactive.Subjects;
using Avalonia.Threading;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.PullRequests;
using Capacitor.Cli.Core.PullRequests.Readers;
using Microsoft.Extensions.Time.Testing;
using static Capacitor.App.Tests.Unit.AvaloniaSession;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit;

[NotInParallel("AvaloniaSession")]
public class PullRequestContextViewModelTests {
    [Test]
    public Task An_empty_list_keeps_its_empty_state_while_polling_until_a_PR_is_found() => RunOnUiAsync(async () => {
        var h = new Harness();
        try {
            h.Source.Links = [];
            h.Source.ListGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            h.Push();
            h.Vm.SetForeground(true);
            await WaitUntilAsync(() => h.Source.Lists > 0, what: "initial discovery started");
            await Assert.That(h.Vm.Notice).IsEqualTo("No pull requests yet.");
            await Assert.That(h.Vm.SectionMeta).IsEmpty();

            h.Source.ListGate.SetResult();
            await WaitUntilAsync(() => h.Vm.HasListed && !h.Vm.IsReading, what: "empty discovery settled");
            await Assert.That(h.Vm.Notice).IsEqualTo("No pull requests yet.");
            await Assert.That(h.Vm.HasNotice).IsTrue();

            h.Source.ListGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var lists = h.Source.Lists;
            h.Time.Advance(TimeSpan.FromSeconds(30));
            await WaitUntilAsync(() => h.Source.Lists > lists, what: "background poll started");
            await Assert.That(h.Vm.Notice).IsEqualTo("No pull requests yet.");
            await Assert.That(h.Vm.SectionMeta).IsEmpty();
            await Assert.That(h.Vm.ShowsRefreshing).IsFalse();

            h.Source.Links = [Link(1)];
            h.Source.ListGate.SetResult();
            await WaitUntilAsync(() => h.Vm.HasPullRequest && !h.Vm.IsReading, what: "PR discovered");
            await Assert.That(h.Vm.Notice).IsEmpty();
            await Assert.That(h.Vm.SectionMeta).IsEqualTo("1");
        } finally {
            h.Source.ListGate?.TrySetResult();
            await h.Dispose();
        }
    });

    [Test]
    public Task Link_changes_notify_the_rail_for_initial_discovery_attachments_and_removals() => RunOnUiAsync(async () => {
        var notifications = new List<string>();
        var h = new Harness(linksChanged: notifications.Add);
        try {
            h.Source.Links = [];
            h.Source.AccessSeconds = 300;
            h.Push();
            h.Vm.SetForeground(true);
            await WaitUntilAsync(() => h.Vm.HasListed && !h.Vm.IsReading, what: "initial empty list");
            await Assert.That(notifications.Count).IsEqualTo(1);

            h.Source.Links = [Link(1)];
            h.Time.Advance(TimeSpan.FromSeconds(30));
            await WaitUntilAsync(() => h.Vm.HasPullRequest && !h.Vm.IsReading, what: "attachment discovered by poll");
            await Assert.That(notifications.Count).IsEqualTo(2);

            h.Source.Links = [Link(1), Link(2)];
            h.Time.Advance(TimeSpan.FromSeconds(30));
            await WaitUntilAsync(() => h.Vm.Choices.Count == 2 && !h.Vm.IsReading, what: "second attachment");
            await Assert.That(notifications.Count).IsEqualTo(3);

            h.Source.Links = [Link(2) with { Title = "Updated title" }, Link(1)];
            var lists = h.Source.Lists;
            h.Time.Advance(TimeSpan.FromSeconds(30));
            await WaitUntilAsync(() => h.Source.Lists > lists && !h.Vm.IsReading, what: "same links reordered");
            await Assert.That(notifications.Count).IsEqualTo(3);

            h.Source.Links = [];
            h.Time.Advance(TimeSpan.FromSeconds(3));
            h.Vm.Refresh();
            await WaitUntilAsync(() => !h.Vm.HasPullRequest && !h.Vm.IsReading, what: "removal discovered by manual refresh");
            await Assert.That(h.Vm.Notice).IsEqualTo("No pull requests yet.");
            await Assert.That(h.Vm.SectionMeta).IsEmpty();
            await Assert.That(notifications).IsEquivalentTo(new[] { "session", "session", "session", "session" });
        } finally { await h.Dispose(); }
    });

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public Task A_repository_updated_during_branch_lookup_is_described_to_the_registry(bool initiallyKnown) => RunOnUiAsync(async () => {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var finished = new ManualResetEventSlim();
        var latest = new PullRequestRepository("github", "github.com", "example", "latest", "latest-hash");
        PullRequestRepository? repository = initiallyKnown
            ? new("github", "github.com", "example", "earlier", "earlier-hash") : null;
        var h = new Harness(primary: () => repository, readBranch: _ => {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(10));
            finished.Set();
            return "feature";
        });
        try {
            h.Push();
            h.Vm.SetForeground(true);
            await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(5)));
            await Assert.That(entered.IsSet).IsTrue();
            repository = latest;
            release.Set();
            await WaitUntilAsync(() => h.Vm.CanReveal && !h.Vm.IsReading, what: "current repository described");
            await Assert.That(h.Source.Descriptions.Single().Repository).IsEqualTo(latest);
        } finally {
            release.Set();
            if (entered.IsSet) await Task.Run(() => finished.Wait(TimeSpan.FromSeconds(5)));
            await h.Dispose();
        }
    });

    /// A list read before the work context named the primary repository described the session
    /// without one; the hint arriving lists again, inside the 30-second poll.
    [Test]
    public Task The_primary_repository_arriving_lists_again_with_it() => RunOnUiAsync(async () => {
        PullRequestRepository? repository = null;
        var h = new Harness(primary: () => repository);
        try {
            h.Push();
            await h.Show();
            await WaitUntilAsync(() => !h.Vm.IsReading, what: "the first list");
            repository = new("github", "github.com", "example", "repo", "hash");

            h.Vm.PrimaryRepositoryChanged();
            h.Time.Advance(TimeSpan.FromSeconds(3));

            await WaitUntilAsync(() => h.Source.Lists == 2 && !h.Vm.IsReading, what: "the second list");
            await Assert.That(h.Source.Descriptions.Select(d => d.Repository)).IsEquivalentTo(new[] { null, repository });
        } finally {
            await h.Dispose();
        }
    });

    /// A refresh queued behind the short gap cannot run while the pane is in the background, so
    /// hiding the pane drops it; the return lists afresh with the current repository.
    [Test]
    public Task A_refresh_queued_when_the_pane_is_hidden_is_dropped_until_it_returns() => RunOnUiAsync(async () => {
        PullRequestRepository? repository = null;
        var h = new Harness(primary: () => repository);
        try {
            h.Push();
            await h.Show();
            await WaitUntilAsync(() => !h.Vm.IsReading, what: "the first list");
            repository = new("github", "github.com", "example", "repo", "hash");
            h.Vm.PrimaryRepositoryChanged();
            await Assert.That(h.Vm.IsReading).IsTrue();

            h.Vm.SetForeground(false);
            await Assert.That(h.Vm.IsReading).IsFalse();

            h.Vm.SetForeground(true);
            await WaitUntilAsync(() => h.Source.Lists == 2, what: "the list on return");
            await Assert.That(h.Source.Descriptions[^1].Repository).IsEqualTo(repository);
        } finally {
            await h.Dispose();
        }
    });

    /// A refresh queued behind a list read that then stops retries has nothing left to run it, so
    /// it must not keep the card reading.
    [Test]
    public Task A_refresh_queued_behind_a_read_that_stops_retries_is_dropped() => RunOnUiAsync(async () => {
        var h = new Harness(primary: () => new PullRequestRepository("github", "github.com", "example", "repo", "hash"));
        try {
            h.Source.ListGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            h.Source.ListFailure = "retries_stopped";
            h.Push();
            h.Vm.SetForeground(true);
            await WaitUntilAsync(() => h.Source.Lists == 1, what: "the list read");

            h.Vm.PrimaryRepositoryChanged();
            h.Source.ListGate.SetResult();

            await WaitUntilAsync(() => h.Vm.Notice.StartsWith("This session's pull requests are unavailable", StringComparison.Ordinal), what: "retries stopped");
            await Assert.That(h.Vm.IsReading).IsFalse();
        } finally {
            await h.Dispose();
        }
    });

    /// A card rebuilt for a session it already showed has its list and overview before any read
    /// lands, and still lists again.
    [Test]
    public Task A_reopened_session_shows_its_last_card_while_it_reads_again() => RunOnUiAsync(async () => {
        var snapshots = new LastReads<PullRequestSnapshot>();
        var first = new Harness(snapshots: snapshots);
        try {
            first.Push();
            await first.Show();
        } finally {
            await first.Dispose();
        }

        var second = new Harness(snapshots: snapshots);
        try {
            second.Source.ListGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            second.Push();
            second.Vm.SetForeground(true);

            await Assert.That(second.Vm.CanReveal).IsTrue();
            await Assert.That(second.Vm.HasListed).IsTrue();
            await WaitUntilAsync(() => second.Source.Lists == 1, what: "the list read again");
            second.Source.ListGate.SetResult();
        } finally {
            await second.Dispose();
        }
    });

    [Test]
    public Task Teardown_does_not_wait_for_a_stalled_branch_lookup_or_publish_its_result() => RunOnUiAsync(async () => {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var finished = new ManualResetEventSlim();
        var h = new Harness(readBranch: _ => {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(10));
            finished.Set();
            return "late";
        });
        Task? teardown = null;
        try {
            h.Push();
            h.Vm.SetForeground(true);
            await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(5)));
            await Assert.That(entered.IsSet).IsTrue();
            teardown = h.Vm.TeardownAsync();
            await teardown.WaitAsync(TimeSpan.FromSeconds(2));
            release.Set();
            await Task.Run(() => finished.Wait(TimeSpan.FromSeconds(5)));
            await Assert.That(finished.IsSet).IsTrue();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            await Assert.That(h.Source.Descriptions).IsEmpty();
            await Assert.That(h.Source.Lists).IsEqualTo(0);
            await Assert.That(h.Vm.Choices).IsEmpty();
        } finally {
            release.Set();
            await Task.Run(() => finished.Wait(TimeSpan.FromSeconds(5)));
            if (teardown is not null) await teardown;
            await h.Dispose();
        }
    });

    [Test]
    public Task A_cancelled_branch_lookup_cannot_replace_the_new_registry_context() => RunOnUiAsync(async () => {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var finished = new ManualResetEventSlim();
        var calls = 0;
        var h = new Harness(readBranch: _ => {
            if (Interlocked.Increment(ref calls) != 1) return "new";
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(10));
            finished.Set();
            return "old";
        });
        try {
            h.Push();
            h.Vm.SetForeground(true);
            await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(5)));
            await Assert.That(entered.IsSet).IsTrue();
            h.SignIn.OnNext(System.Reactive.Unit.Default);
            await WaitUntilAsync(() => h.Vm.CanReveal && !h.Vm.IsReading, what: "replacement refresh");
            release.Set();
            await h.Vm.TeardownAsync();
            await Task.Run(() => finished.Wait(TimeSpan.FromSeconds(5)));
            await Assert.That(finished.IsSet).IsTrue();
            await Assert.That(h.Source.Descriptions.Select(item => item.Branch)).IsEquivalentTo(new string?[] { "new" });
        } finally {
            release.Set();
            if (entered.IsSet) await Task.Run(() => finished.Wait(TimeSpan.FromSeconds(5)));
            await h.Dispose();
        }
    });

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public Task A_new_presence_branch_survives_an_unreadable_checkout_lookup(bool duringLookup) => RunOnUiAsync(async () => {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var h = new Harness(readBranch: _ => {
            entered.Set();
            if (duringLookup) release.Wait(TimeSpan.FromSeconds(10));
            return null;
        });
        if (!duringLookup) h.Source.ListGate = new();
        try {
            h.Push();
            h.Vm.SetForeground(true);
            await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(5)));
            await Assert.That(entered.IsSet).IsTrue();
            if (!duringLookup) await WaitUntilAsync(() => h.Source.Lists == 1, what: "lookup completed, list pending");
            h.Presence.OnNext(Agent("agent", "claude", hasTerminal: false, sessionId: "session", branch: "another"));
            h.Source.Links = [FakePullRequestSource.Link(1) with { HeadRef = "another" }, FakePullRequestSource.Link(2)];
            release.Set();
            h.Source.ListGate?.TrySetResult();
            await WaitUntilAsync(() => h.Vm.CanReveal && !h.Vm.IsReading, what: "new branch selected");
            await Assert.That(h.Vm.Selected!.Subject.Number).IsEqualTo(1);
            if (duringLookup) await Assert.That(h.Source.Descriptions.Single().Branch).IsEqualTo("another");
        } finally { release.Set(); h.Source.ListGate?.TrySetResult(); await h.Dispose(); }
    });

    /// A body's GitHub references link only into a github.com repository; elsewhere they stay text.
    [Test]
    [Arguments("github", "github.com", "https://github.com/example/repo")]
    [Arguments("github", "ghe.example", null)]
    [Arguments("gitlab", "gitlab.com", null)]
    public Task References_read_against_the_selected_github_repository(string provider, string host, string? expected) => RunOnUiAsync(async () => {
        var h = new Harness();
        h.Source.Links = [FakePullRequestSource.Link(1) with { Provider = provider, Host = host }];
        h.Push();
        h.Vm.SetForeground(true);
        await WaitUntilAsync(() => h.Vm.Selected is not null, what: "a selected PR");
        await Assert.That(h.Vm.ReferenceBase).IsEqualTo(expected);
        await h.Dispose();
    });

    [Test]
    public Task Repeated_refreshes_in_the_throttle_window_do_not_rebind_unchanged_properties() => RunOnUiAsync(async () => {
        var h = new Harness();
        h.Push();
        await h.Show();
        await WaitUntilAsync(() => !h.Vm.IsReading, what: "settled overview");
        h.Vm.Refresh();
        var changed = new List<string?>();
        h.Vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        for (var i = 0; i < 10; i++) h.Vm.Refresh();
        await Assert.That(changed).IsEmpty();
        h.Time.Advance(TimeSpan.FromSeconds(3));
        await WaitUntilAsync(() => !h.Vm.IsReading, what: "queued refresh");
        await Assert.That(h.Source.Lists).IsEqualTo(2);
        await h.Dispose();
    });

    /// A discovery that failed during an outage backs off for minutes; the server coming back clears that backoff,
    /// so the panel discovers again at once instead of keeping the failure.
    [Test]
    public Task A_reconnect_rediscovers_past_the_outage_backoff() => RunOnUiAsync(async () => {
        var h = new Harness();
        h.Source.Capability = PullRequestCapabilityKind.Unavailable;
        h.Source.CapabilityRetryAt = h.Time.GetUtcNow().UtcDateTime.AddMinutes(5);
        h.Push();
        h.Vm.SetForeground(true);
        await WaitUntilAsync(() => h.Source.Discoveries >= 1 && !h.Vm.IsReading, what: "failed discovery");
        await Assert.That(h.Vm.CanReveal).IsFalse();

        h.Source.Capability = PullRequestCapabilityKind.Supported;
        h.Source.CapabilityRetryAt = null;
        var discoveries = h.Source.Discoveries;
        h.Vm.Reconnected();

        await WaitUntilAsync(() => h.Vm.CanReveal && !h.Vm.IsReading, what: "rediscovered on reconnect");
        await Assert.That(h.Source.Discoveries).IsGreaterThan(discoveries);
        await h.Dispose();
    });

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public Task An_explicit_pr_choice_survives_sign_in(bool alreadyListed) => RunOnUiAsync(async () => {
        var h = new Harness();
        h.Push();
        if (alreadyListed) await h.Show();
        h.Vm.SelectSubject(PullRequestWire.Subject(FakePullRequestSource.Link(1)));
        h.Source.Capability = PullRequestCapabilityKind.SignedOut;
        if (alreadyListed) { h.Time.Advance(TimeSpan.FromSeconds(4)); h.Vm.Refresh(); }
        else h.Vm.SetForeground(true);
        await WaitUntilAsync(() => h.Vm.Notice == "Sign in to see pull requests.", what: "sign-in notice");
        await Assert.That(h.Vm.Choices).Count().IsEqualTo(0);
        await Assert.That(h.Vm.CanReveal).IsFalse();
        h.Source.Capability = PullRequestCapabilityKind.Supported;
        h.SignIn.OnNext(System.Reactive.Unit.Default);
        await WaitUntilAsync(() => h.Vm.CanReveal && !h.Vm.IsReading, what: "signed-in PR reader");
        await Assert.That(h.Vm.Selected!.Subject.Number).IsEqualTo(1);
        h.Vm.Selected = h.Vm.Choices.Single(choice => choice.Subject.Number == 2);
        h.Time.Advance(TimeSpan.FromSeconds(4));
        h.Source.Capability = PullRequestCapabilityKind.SignedOut;
        h.Vm.Refresh();
        await WaitUntilAsync(() => h.Vm.Notice == "Sign in to see pull requests.", what: "second sign-out");
        h.Source.Capability = PullRequestCapabilityKind.Supported;
        h.SignIn.OnNext(System.Reactive.Unit.Default);
        await WaitUntilAsync(() => h.Vm.CanReveal && !h.Vm.IsReading, what: "new choice recovered");
        await Assert.That(h.Vm.Selected!.Subject.Number).IsEqualTo(2);
        await h.Dispose();
    });

    [Test]
    public Task An_explicit_choice_from_a_previous_session_is_not_reused() => RunOnUiAsync(async () => {
        var h = new Harness();
        h.Push();
        h.Vm.SelectSubject(PullRequestWire.Subject(FakePullRequestSource.Link(1)));
        await h.Show();
        h.Presence.OnNext(Agent("agent", "claude", hasTerminal: false, sessionId: "another-session", branch: "feature"));
        await WaitUntilAsync(() => h.Vm.CanReveal && !h.Vm.IsReading, what: "new session reader");
        await Assert.That(h.Vm.Selected!.Subject.Number).IsEqualTo(2);
        await h.Dispose();
    });

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public Task A_menu_choice_survives_initial_session_and_link_loading(bool beforePresence) => RunOnUiAsync(async () => {
        var h = new Harness();
        if (!beforePresence) h.Push();
        h.Vm.SelectSubject(PullRequestWire.Subject(FakePullRequestSource.Link(1)));
        if (beforePresence) h.Push();
        await h.Show();
        await Assert.That(h.Vm.Selected!.Subject.Number).IsEqualTo(1);
        await Assert.That(h.Vm.Selected.IsAvailable).IsTrue();
        await h.Dispose();
    });

    /// A PR pinned before discovery settles is the card's subject, so the empty message never sits beside it.
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public Task A_menu_choice_pinned_during_discovery_hides_the_empty_message(bool beforePresence) => RunOnUiAsync(async () => {
        var h = new Harness();
        try {
            h.Source.ListGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!beforePresence) h.Push();
            h.Vm.SelectSubject(PullRequestWire.Subject(FakePullRequestSource.Link(1)));
            if (beforePresence) h.Push();
            h.Vm.SetForeground(true);
            await WaitUntilAsync(() => h.Source.Lists > 0, what: "initial discovery started");
            await Assert.That(h.Vm.Selected!.Subject.Number).IsEqualTo(1);
            await Assert.That(h.Vm.Notice).IsEmpty();
        } finally {
            h.Source.ListGate?.TrySetResult();
            await h.Dispose();
        }
    });

    [Test]
    public Task A_menu_choice_removed_from_the_list_does_not_open_another_pr() => RunOnUiAsync(async () => {
        var h = new Harness();
        h.Vm.SelectSubject(PullRequestWire.Subject(FakePullRequestSource.Link(99)));
        h.Push();
        h.Vm.SetForeground(true);
        await WaitUntilAsync(() => h.Vm.HasListed, what: "links loaded");
        await Assert.That(h.Vm.Selected!.Subject.Number).IsEqualTo(99);
        await Assert.That(h.Vm.Selected.IsAvailable).IsFalse();
        await Assert.That(h.Vm.CanReveal).IsFalse();
        await h.Dispose();
    });

    [Test]
    public Task Reads_start_only_in_the_foreground_and_focus_loss_masks_already_loaded_bodies() => RunOnUiAsync(async () => {
        var h = new Harness();
        h.Push();
        await Assert.That(h.Source.Lists).IsEqualTo(0);
        await h.Show();
        h.Vm.SetReaderVisible(true);
        await Assert.That(h.Vm.Description).IsEqualTo("Private description");
        h.Vm.SetForeground(false);
        await Assert.That(h.Vm.Description).IsNull();
        await Assert.That(h.Vm.Rows).IsEmpty();
        await Assert.That(h.Vm.CanReveal).IsFalse();
        var calls = h.Source.Overviews;
        h.Time.Advance(TimeSpan.FromMinutes(1));
        Dispatcher.UIThread.RunJobs();
        await Assert.That(h.Source.Overviews).IsEqualTo(calls);
        await h.Dispose();
    });

    [Test]
    public Task The_host_exit_waits_until_the_overview_or_a_legacy_settle() => RunOnUiAsync(async () => {
        var h = new Harness();
        var overview = new TaskCompletionSource<PullRequestRead<PullRequestOverviewDto>>();
        h.Source.OverviewResponses.Enqueue((_, _) => overview.Task);
        h.Push();
        h.Vm.SetForeground(true);
        await WaitUntilAsync(() => h.Vm.HasChoice && h.Source.Overviews == 1, what: "choice listed, overview pending");
        await Assert.That(h.Vm.ShowsOpenSource).IsFalse();
        await Assert.That(h.Vm.IsSelectionLoading).IsTrue();
        await Assert.That(h.Vm.HasNotice).IsFalse();
        overview.SetResult(h.Source.Overview(h.Vm.Selected!.Subject));
        await WaitUntilAsync(() => h.Vm.CanDisplay, what: "overview admitted");
        await Assert.That(h.Vm.ShowsOpenSource).IsTrue();
        await h.Dispose();
    });

    [Test]
    public Task A_legacy_list_shows_the_host_exit_without_an_overview() => RunOnUiAsync(async () => {
        var h = new Harness();
        h.Source.Capability = PullRequestCapabilityKind.Legacy;
        h.Push();
        h.Vm.SetForeground(true);
        await WaitUntilAsync(() => h.Vm.HasListed && h.Vm.IsLegacy, what: "legacy list applied");
        await Assert.That(h.Vm.ShowsOpenSource).IsTrue();
        await h.Dispose();
    });

    [Test]
    public Task A_B_A_selection_cancels_the_old_request_and_rejects_its_late_result() => RunOnUiAsync(async () => {
        var h = new Harness();
        var old = new TaskCompletionSource<PullRequestRead<PullRequestOverviewDto>>();
        h.Source.OverviewResponses.Enqueue((_, _) => old.Task);
        h.Push(); h.Vm.SetForeground(true);
        await WaitUntilAsync(() => h.Source.Overviews == 1, what: "first overview request");
        var a = h.Vm.Selected!; var b = h.Vm.Choices.Single(choice => choice != a);
        h.Vm.Selected = b;
        await WaitUntilAsync(() => h.Vm.CanReveal, what: "B admitted");
        h.Source.OverviewTitle = "Current A";
        h.Vm.Selected = a;
        await WaitUntilAsync(() => h.Vm.Title == "Current A", what: "new A admitted");
        old.SetResult(h.Source.Overview(a.Subject, "Old A"));
        await WaitUntilAsync(() => !h.Vm.IsReading, what: "old request settles");
        Dispatcher.UIThread.RunJobs();
        await Assert.That(h.Source.OverviewTokens[0].IsCancellationRequested).IsTrue();
        await Assert.That(h.Vm.Title).IsEqualTo("Current A");
        await h.Dispose();
    });

    [Test]
    public Task Transient_grace_retains_cached_sections_and_cannot_open_an_uncached_section() => RunOnUiAsync(async () => {
        var h = new Harness(); h.Push(); await h.Show();
        h.Vm.SetReaderVisible(true);
        await h.Vm.ShowSectionCommand.Execute("checks");
        await WaitUntilAsync(() => h.Vm.Rows.Count == 1, what: "checks page cached");
        await h.Vm.ShowSectionCommand.Execute("overview");
        h.Source.Failure = "transient";
        h.Time.Advance(TimeSpan.FromSeconds(21));
        await h.Vm.RefreshCommand.Execute();
        await WaitUntilAsync(() => !h.Vm.IsReading, what: "access refresh fails");
        await Assert.That(h.Vm.CanReveal).IsFalse();
        await Assert.That(h.Vm.CanNavigate).IsTrue();
        await Assert.That(h.Vm.HasNotice).IsFalse();
        await Assert.That(h.Vm.FreshnessDetail).Contains("Couldn't load pull request context");
        await Assert.That(h.Vm.Description).IsEqualTo("Private description");
        await h.Vm.ShowSectionCommand.Execute("reviews");
        await WaitUntilAsync(() => !h.Vm.IsReading, what: "overview renewal attempted");
        await Assert.That(h.Source.Pages).IsEqualTo(1);
        await Assert.That(h.Vm.Section).IsEqualTo("reviews");
        await Assert.That(h.Vm.Rows).IsEmpty();
        await Assert.That(h.Vm.PageNote).Contains("Couldn't load pull request context");
        h.Source.Failure = null;
        await h.Vm.ShowSectionCommand.Execute("conversation");
        await WaitUntilAsync(() => h.Vm.Rows.Count == 1 && !h.Vm.IsReading, what: "clicking uncached tab renews access and loads section");
        await Assert.That(h.Vm.Section).IsEqualTo("conversation");
        await Assert.That(h.Vm.PageNote).IsEmpty();
        h.Source.Failure = "transient";
        h.Time.Advance(TimeSpan.FromSeconds(21));
        await h.Vm.RefreshCommand.Execute();
        await WaitUntilAsync(() => !h.Vm.IsReading, what: "access refresh fails again");
        await h.Vm.ShowSectionCommand.Execute("checks");
        await Assert.That(h.Source.Pages).IsEqualTo(2);
        await Assert.That(h.Vm.Section).IsEqualTo("checks");
        await Assert.That(h.Vm.Rows.Count).IsEqualTo(1);
        h.Vm.SetReaderVisible(false); h.Vm.SetReaderVisible(true);
        await Assert.That(h.Vm.Rows.Count).IsEqualTo(1);
        h.Time.Advance(TimeSpan.FromMinutes(6)); Dispatcher.UIThread.RunJobs();
        await WaitUntilAsync(() => !h.Vm.IsReading, what: "grace expires");
        await Assert.That(h.Vm.CanDisplay).IsFalse();
        await h.Dispose();
    });

    [Test]
    [Arguments(6, true)]
    [Arguments(306, false)]
    public Task A_slow_renewal_retains_the_snapshot_only_within_the_display_grace(int seconds, bool visible) => RunOnUiAsync(async () => {
        var h = new Harness(); h.Push(); await h.Show(); h.Vm.SetReaderVisible(true);
        var renewal = new TaskCompletionSource<PullRequestRead<PullRequestOverviewDto>>();
        h.Source.OverviewResponses.Enqueue((_, _) => renewal.Task);
        h.Time.Advance(TimeSpan.FromSeconds(25));
        await WaitUntilAsync(() => h.Source.Overviews == 2, what: "renewal started");
        h.Time.Advance(TimeSpan.FromSeconds(seconds));
        Dispatcher.UIThread.RunJobs();
        await Assert.That(h.Vm.CanDisplay).IsEqualTo(visible);
        await Assert.That(h.Vm.Description).IsEqualTo(visible ? "Private description" : null);
        await Assert.That(h.Vm.HasStaleOverview).IsFalse();
        await Assert.That(h.Vm.CanReveal).IsFalse();
        renewal.SetResult(h.Source.Overview(h.Vm.Selected!.Subject));
        await WaitUntilAsync(() => !h.Vm.IsReading, what: "renewal settled");
        await Assert.That(h.Vm.CanReveal).IsTrue();
        await h.Dispose();
    });

    [Test]
    public Task A_denial_cancels_a_pending_page_so_it_cannot_restore_protected_content() => RunOnUiAsync(async () => {
        var h = new Harness(); h.Push(); await h.Show(); h.Vm.SetReaderVisible(true);
        var page = new TaskCompletionSource();
        h.Source.PageGate = page;
        await h.Vm.ShowSectionCommand.Execute("checks");
        await WaitUntilAsync(() => h.Source.Pages == 1, what: "page started");
        h.Source.Failure = "denied";
        h.Time.Advance(TimeSpan.FromSeconds(25));
        await WaitUntilAsync(() => h.Vm.Notice.Contains("cannot read"), what: "renewal denied");
        await Assert.That(h.Source.OverviewTokens[^1].IsCancellationRequested).IsTrue();
        page.SetResult();
        await Task.Yield(); Dispatcher.UIThread.RunJobs();
        await Assert.That(h.Vm.CanDisplay).IsFalse();
        await Assert.That(h.Vm.Rows).IsEmpty();
        await h.Dispose();
    });

    [Test]
    public Task A_list_completion_does_not_clear_the_overviews_retry_deadline() => RunOnUiAsync(async () => {
        var h = new Harness(); h.Push(); await h.Show(); h.Vm.SetReaderVisible(true);
        var list = new TaskCompletionSource();
        h.Source.ListGate = list;
        h.Source.OverviewResponses.Enqueue((subject, _) => Task.FromResult(h.Source.Overview(subject) with {
            Kind = PullRequestReadKind.Stale, RetryAt = h.Time.GetUtcNow().UtcDateTime.AddMinutes(2), Reason = "rate_limited"
        }));
        h.Time.Advance(TimeSpan.FromSeconds(30));
        await WaitUntilAsync(() => h.Vm.HasStaleOverview, what: "overview cooldown");
        list.SetResult();
        await WaitUntilAsync(() => !h.Vm.IsReading, what: "list completion");
        await h.Vm.ShowSectionCommand.Execute("checks");
        await Assert.That(h.Source.Pages).IsEqualTo(0);
        await h.Dispose();
    });

    [Test]
    [Arguments("capacity_exhausted", false)]
    [Arguments("capacity_exhausted", true)]
    [Arguments("rate_limited", false)]
    [Arguments("rate_limited", true)]
    [Arguments("budget_exhausted", false)]
    [Arguments("budget_exhausted", true)]
    public Task List_refreshes_obey_host_cooldowns_but_not_content_retry_deadlines(string reason, bool manual) => RunOnUiAsync(async () => {
        var h = new Harness();
        try {
            h.Push(); await h.Show();
            h.Source.OverviewResponses.Enqueue((subject, _) => Task.FromResult(new PullRequestRead<PullRequestOverviewDto>(
                PullRequestReadKind.Unavailable, Subject: subject, Reason: reason, RetryAt: h.Time.GetUtcNow().UtcDateTime.AddMinutes(2))));
            h.Time.Advance(TimeSpan.FromSeconds(25));
            await WaitUntilAsync(() => h.Source.Overviews == 2 && !h.Vm.IsReading, what: "overview retry received");
            await Assert.That(h.Source.Lists).IsEqualTo(1);

            h.Time.Advance(TimeSpan.FromSeconds(manual ? 4 : 5));
            Dispatcher.UIThread.RunJobs();
            if (manual) h.Vm.Refresh();
            await WaitUntilAsync(() => !h.Vm.IsReading, what: "list refresh settled");
            await Assert.That(h.Source.Lists).IsEqualTo(reason == "capacity_exhausted" ? 2 : 1);
            await Assert.That(h.Source.Overviews).IsEqualTo(2);
        } finally { await h.Dispose(); }
    });

    [Test]
    public Task A_transient_list_failure_respects_retry_after_and_keeps_a_fresh_overview() => RunOnUiAsync(async () => {
        var h = new Harness(); h.Push(); await h.Show();
        h.Source.ListResponses.Enqueue(new(PullRequestReadKind.Unavailable, AccessFailure: "transient",
            RetryAt: h.Time.GetUtcNow().UtcDateTime.AddMinutes(2), Reason: "rate_limited"));
        h.Time.Advance(TimeSpan.FromSeconds(30));
        await WaitUntilAsync(() => h.Source.Lists == 2 && !h.Vm.IsReading, what: "list failure and overview renewal");
        await Assert.That(h.Vm.HasStaleOverview).IsFalse();
        h.Time.Advance(TimeSpan.FromSeconds(31)); Dispatcher.UIThread.RunJobs();
        await Assert.That(h.Source.Lists).IsEqualTo(2);
        await h.Dispose();
    });

    [Test]
    [Arguments("transient")]
    [Arguments("denied")]
    public Task An_unavailable_snapshot_does_not_wake_the_timer_every_second(string failure) => RunOnUiAsync(async () => {
        var h = new Harness(); h.Push(); await h.Show();
        h.Source.Failure = failure;
        h.Time.Advance(TimeSpan.FromSeconds(25));
        await WaitUntilAsync(() => !h.Vm.IsReading, what: "failed renewal settled");
        var before = h.Clock.Callbacks;
        for (var i = 0; i < 25; i++) {
            h.Time.Advance(TimeSpan.FromSeconds(1)); Dispatcher.UIThread.RunJobs();
            await WaitUntilAsync(() => !h.Vm.IsReading, what: "poll settled");
        }
        await Assert.That(h.Clock.Callbacks - before).IsLessThanOrEqualTo(2);
        await h.Dispose();
    });

    [Test]
    public Task A_new_session_does_not_inherit_the_previous_sessions_retry_deadline() => RunOnUiAsync(async () => {
        var h = new Harness();
        h.Source.OverviewResponses.Enqueue((subject, _) => Task.FromResult(new PullRequestRead<PullRequestOverviewDto>(
            PullRequestReadKind.Unavailable, Subject: subject, AccessFailure: "denied", Reason: "github_access_denied",
            RetryAt: h.Time.GetUtcNow().UtcDateTime.AddMinutes(2))));
        h.Push(); h.Vm.SetForeground(true);
        await WaitUntilAsync(() => h.Vm.Notice.Contains("cannot read"), what: "first session denied");
        h.Presence.OnNext(Agent("agent", "claude", hasTerminal: false, sessionId: "other-session", branch: "feature"));
        await WaitUntilAsync(() => h.Vm.CanReveal && !h.Vm.IsReading, what: "new session loaded");
        await Assert.That(h.Source.Lists).IsEqualTo(2);
        await h.Dispose();
    });

    [Test]
    public Task Thirty_second_background_poll_renews_overview_without_flashing_grace_or_stale() => RunOnUiAsync(async () => {
        var h = new Harness(); h.Push(); await h.Show();
        await WaitUntilAsync(() => !h.Vm.IsReading, what: "initial overview settled");
        var sawStale = false;
        h.Vm.PropertyChanged += (_, e) => {
            if (e.PropertyName == nameof(PullRequestContextViewModel.HasStaleOverview) && h.Vm.HasStaleOverview)
                sawStale = true;
        };
        var slowList = new TaskCompletionSource();
        h.Source.ListGate = slowList;
        h.Time.Advance(TimeSpan.FromSeconds(30));
        Dispatcher.UIThread.RunJobs();
        await WaitUntilAsync(() => h.Source.Overviews == 2, what: "overview renewed on 30s tick while list refresh is in flight");
        await Assert.That(sawStale).IsFalse();
        await Assert.That(h.Vm.HasStaleOverview).IsFalse();
        await Assert.That(h.Vm.CanReveal).IsTrue();
        slowList.SetResult();
        await WaitUntilAsync(() => !h.Vm.IsReading, what: "list refresh completes");
        await Assert.That(sawStale).IsFalse();
        await h.Dispose();
    });

    [Test]
    public Task Refreshing_on_checks_with_a_new_head_keeps_existing_rows_visible_until_replacement_arrives() => RunOnUiAsync(async () => {
        var h = new Harness(); h.Push(); await h.Show();
        h.Vm.SetReaderVisible(true);
        await h.Vm.ShowSectionCommand.Execute("checks");
        await WaitUntilAsync(() => h.Vm.Rows.Count == 1, what: "initial checks page");
        var emptied = false;
        h.Vm.PropertyChanged += (_, e) => {
            if (e.PropertyName == nameof(PullRequestContextViewModel.Rows) && h.Vm.Rows.Count == 0)
                emptied = true;
        };
        var pageGate = new TaskCompletionSource();
        h.Source.PageGate = pageGate;
        h.Source.HeadSha = new string('b', 40);
        h.Time.Advance(TimeSpan.FromSeconds(16));
        await h.Vm.RefreshCommand.Execute();
        await WaitUntilAsync(() => h.Source.Pages == 2, what: "replacement checks page requested");
        await Assert.That(h.Vm.Rows.Count).IsEqualTo(1);
        await Assert.That(emptied).IsFalse();
        pageGate.SetResult();
        await WaitUntilAsync(() => !h.Vm.IsReading, what: "replacement checks page applied");
        await Assert.That(h.Vm.Rows.Count).IsEqualTo(1);
        await Assert.That(emptied).IsFalse();
        await h.Dispose();
    });

    [Test]
    public Task An_explicit_denial_clears_previously_visible_content_immediately() => RunOnUiAsync(async () => {
        var h = new Harness(); h.Push(); await h.Show(); h.Vm.SetReaderVisible(true);
        h.Source.Failure = "denied"; h.Time.Advance(TimeSpan.FromSeconds(16));
        await h.Vm.RefreshCommand.Execute();
        // A refresh lists before it reads the overview, and IsReading drops between the two: wait for the denial itself.
        await WaitUntilAsync(() => h.Vm.Notice?.Contains("cannot read") == true, what: "denial applied");
        await Assert.That(h.Vm.Description).IsNull();
        await Assert.That(h.Vm.CanDisplay).IsFalse();
        await Assert.That(h.Vm.Notice).Contains("cannot read");
        await h.Dispose();
    });

    [Test]
    public Task Explicit_selection_survives_list_refreshes_and_body_state_does_not_survive_PR_switches() => RunOnUiAsync(async () => {
        var h = new Harness(); h.Push(); await h.Show(); h.Vm.SetReaderVisible(true);
        h.Vm.Selected = h.Vm.Choices[0];
        await WaitUntilAsync(() => h.Vm.CanReveal, what: "selected the older PR");
        await h.Vm.ShowSectionCommand.Execute("conversation");
        await WaitUntilAsync(() => h.Vm.Rows.Count == 1, what: "comment page loaded");
        h.Time.Advance(TimeSpan.FromSeconds(16)); await h.Vm.RefreshCommand.Execute();
        await WaitUntilAsync(() => !h.Vm.IsReading, what: "list refresh");
        await Assert.That(h.Vm.Selected!.Subject.Number).IsEqualTo(1);
        h.Vm.Selected = h.Vm.Choices[1];
        await Assert.That(h.Vm.Rows).IsEmpty();
        await h.Dispose();
    });

    [Test]
    [Arguments("checks")]
    [Arguments("threads")]
    public Task A_head_restart_reloads_a_paginated_section_even_when_the_overview_head_is_unchanged(string section) => RunOnUiAsync(async () => {
        var h = new Harness(); h.Push(); await h.Show();
        await h.Vm.ShowSectionCommand.Execute(section);
        await WaitUntilAsync(() => h.Vm.Rows.Count == 1 && !h.Vm.IsReading, what: "first page");
        await h.Vm.LoadMoreCommand.Execute();
        await WaitUntilAsync(() => h.Vm.Rows.Count == 2 && !h.Vm.IsReading, what: "second page");
        var replacement = new TaskCompletionSource();
        h.Source.PageGate = replacement;
        h.Source.RestartNextPage = "head_changed";
        try {
            await h.Vm.LoadMoreCommand.Execute();
            await WaitUntilAsync(() => h.Source.Pages == 4, what: "replacement page one requested");
            await Assert.That(h.Vm.Rows.Count).IsEqualTo(2);
            await Assert.That(h.Vm.HasMore).IsFalse();
            replacement.SetResult();
            await WaitUntilAsync(() => !h.Vm.IsReading, what: "replacement applied");
            await Assert.That(h.Vm.Rows.Count).IsEqualTo(1);
            await Assert.That(h.Vm.HasMore).IsTrue();
            await Assert.That(h.Vm.PageNote).IsEmpty();
        } finally { replacement.TrySetResult(); await h.Dispose(); }
    });

    [Test]
    public Task A_head_restart_still_reloads_its_section_after_the_reader_switches_tabs() => RunOnUiAsync(async () => {
        var h = new Harness(); h.Push(); await h.Show();
        await h.Vm.ShowSectionCommand.Execute("threads");
        await WaitUntilAsync(() => h.Vm.Rows.Count == 1 && !h.Vm.IsReading, what: "first page");
        await h.Vm.LoadMoreCommand.Execute();
        await WaitUntilAsync(() => h.Vm.Rows.Count == 2 && !h.Vm.IsReading, what: "second page");
        var overview = new TaskCompletionSource<PullRequestRead<PullRequestOverviewDto>>();
        h.Source.OverviewResponses.Enqueue((_, _) => overview.Task);
        h.Source.RestartNextPage = "head_changed";
        try {
            await h.Vm.LoadMoreCommand.Execute();
            await WaitUntilAsync(() => h.Source.Overviews == 2, what: "recovery overview pending");
            await h.Vm.ShowSectionCommand.Execute("conversation");
            overview.SetResult(h.Source.Overview(h.Vm.Selected!.Subject));
            await WaitUntilAsync(() => !h.Vm.IsReading, what: "overview settled on another tab");
            await h.Vm.ShowSectionCommand.Execute("threads");
            await WaitUntilAsync(() => !h.Vm.IsReading, what: "return to threads");
            await Assert.That(h.Vm.Rows.Count).IsEqualTo(1);
            await Assert.That(h.Vm.HasMore).IsTrue();
        } finally { overview.TrySetResult(h.Source.Overview(h.Vm.Selected!.Subject)); await h.Dispose(); }
    });

    [Test]
    [Arguments("rate_limited", false)]
    [Arguments("budget_exhausted", false)]
    [Arguments("rate_limited", true)]
    public Task Switching_PRs_preserves_host_cooldowns_without_blocking_another_host(string reason, bool anotherHost) => RunOnUiAsync(async () => {
        var h = new Harness();
        if (anotherHost) h.Source.Links[0] = h.Source.Links[0] with { Host = "ghe.example", Url = "https://ghe.example/example/repo/pull/1" };
        h.Push(); await h.Show();
        h.Source.OverviewResponses.Enqueue((subject, _) => Task.FromResult(new PullRequestRead<PullRequestOverviewDto>(
            PullRequestReadKind.Unavailable, Subject: subject, Reason: reason, RetryAt: h.Time.GetUtcNow().UtcDateTime.AddMinutes(1))));
        h.Time.Advance(TimeSpan.FromSeconds(25));
        await WaitUntilAsync(() => h.Source.Overviews == 2 && !h.Vm.IsReading, what: "rate limit received");
        h.Vm.Selected = h.Vm.Choices.Single(choice => choice.Subject.Number == 1);
        if (anotherHost) {
            await WaitUntilAsync(() => h.Vm.CanReveal && !h.Vm.IsReading, what: "independent host loaded");
        } else {
            await Assert.That(h.Source.Overviews).IsEqualTo(2);
            await Assert.That(h.Vm.Notice).Contains("cooldown");
            h.Time.Advance(TimeSpan.FromSeconds(60)); Dispatcher.UIThread.RunJobs();
            await WaitUntilAsync(() => h.Vm.CanReveal && !h.Vm.IsReading, what: "cooldown expired");
        }
        await Assert.That(h.Source.Overviews).IsEqualTo(3);
        await h.Dispose();
    });

    [Test]
    public Task A_failed_renewal_cannot_restart_grace_after_access_has_expired() => RunOnUiAsync(async () => {
        var h = new Harness(); h.Push(); await h.Show(); h.Vm.SetReaderVisible(true);
        var renewal = new TaskCompletionSource<PullRequestRead<PullRequestOverviewDto>>();
        h.Source.OverviewResponses.Enqueue((_, _) => renewal.Task);
        h.Time.Advance(TimeSpan.FromSeconds(25));
        await WaitUntilAsync(() => h.Source.Overviews == 2, what: "renewal started");
        h.Time.Advance(TimeSpan.FromSeconds(20)); Dispatcher.UIThread.RunJobs();
        h.Source.Failure = "transient";
        renewal.SetResult(new(PullRequestReadKind.TransportFailure, Subject: h.Vm.Selected!.Subject, AccessFailure: "transient", Reason: "timeout"));
        await WaitUntilAsync(() => !h.Vm.IsReading, what: "renewal timed out");
        await Assert.That(h.Vm.CanDisplay).IsTrue();
        h.Time.Advance(TimeSpan.FromSeconds(285)); Dispatcher.UIThread.RunJobs();
        await WaitUntilAsync(() => !h.Vm.IsReading, what: "original grace expired");
        await Assert.That(h.Vm.CanDisplay).IsFalse();
        await Assert.That(h.Vm.Description).IsNull();
        await h.Dispose();
    });

    [Test]
    public Task A_list_refresh_alone_does_not_delay_the_stale_snapshot_indicator() => RunOnUiAsync(async () => {
        var h = new Harness(); h.Push(); await h.Show(); h.Vm.SetReaderVisible(true);
        var renewal = new TaskCompletionSource<PullRequestRead<PullRequestOverviewDto>>();
        var list = new TaskCompletionSource();
        h.Source.ListGate = list;
        h.Source.OverviewResponses.Enqueue((_, _) => renewal.Task);
        h.Time.Advance(TimeSpan.FromSeconds(30));
        await WaitUntilAsync(() => h.Source.Overviews == 2 && h.Source.Lists == 2, what: "renewal and list pending");
        // CanReveal turns true in one UI job but is announced by the renewal's settle job, whose notify re-arms
        // the tick: an advance made before that announcement re-arms it past the advance and fires nothing.
        var announced = false;
        h.Vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(h.Vm.CanReveal) && h.Vm.CanReveal) announced = true; };
        renewal.SetResult(h.Source.Overview(h.Vm.Selected!.Subject) with { AccessValidForSeconds = 10 });
        await WaitUntilAsync(() => announced, what: "short access lease renewed");
        h.Time.Advance(TimeSpan.FromSeconds(10)); Dispatcher.UIThread.RunJobs();
        await WaitUntilAsync(() => h.Vm.HasStaleOverview, what: "list does not renew access");
        await Assert.That(h.Vm.Description).IsEqualTo("Private description");
        list.SetResult();
        await WaitUntilAsync(() => !h.Vm.IsReading, what: "list settled");
        await h.Dispose();
    });

    [Test]
    public Task Switching_to_another_PR_clears_retry_backoff_and_notice_from_the_previous_PR() => RunOnUiAsync(async () => {
        var h = new Harness();
        var retry = h.Time.GetUtcNow().UtcDateTime.AddMinutes(2);
        h.Source.OverviewResponses.Enqueue((subject, _) => Task.FromResult(new PullRequestRead<PullRequestOverviewDto>(
            PullRequestReadKind.Unavailable, Subject: subject, AccessFailure: "denied", Reason: "github_access_denied", RetryAt: retry)));
        h.Push();
        h.Vm.SetForeground(true);
        await WaitUntilAsync(() => h.Vm.Notice.Contains("cannot read"), what: "initial PR failed with retry backoff");
        h.Vm.Selected = h.Vm.Choices[0];
        await Assert.That(h.Vm.HasNotice).IsFalse();
        await WaitUntilAsync(() => h.Vm.CanReveal && h.Vm.Selected?.Subject.Number == 1, what: "second PR loaded immediately despite previous PR RetryAt");
        await h.Dispose();
    });

    [Test]
    public Task A_section_keeps_eight_pages_and_exposes_a_reload_control_for_evicted_content() => RunOnUiAsync(async () => {
        var h = new Harness(); h.Source.TotalPages = 12; h.Push(); await h.Show(); h.Vm.SetReaderVisible(true);
        await h.Vm.ShowSectionCommand.Execute("conversation");
        await WaitUntilAsync(() => !h.Vm.IsReading, what: "first page");
        for (var page = 1; page < 10; page++) {
            await h.Vm.LoadMoreCommand.Execute();
            await WaitUntilAsync(() => !h.Vm.IsReading, what: "next page");
        }
        await Assert.That(h.Vm.Rows.Count).IsEqualTo(8);
        await Assert.That(h.Vm.CanReloadEarlier).IsTrue();
        await Assert.That(h.Vm.HasMore).IsTrue();
        await h.Vm.ReloadEarlierCommand.Execute();
        await WaitUntilAsync(() => !h.Vm.IsReading, what: "evicted page reloaded");
        await Assert.That(h.Vm.Rows[0].Id).IsEqualTo("item-1");
        await Assert.That(h.Vm.Rows.Count).IsEqualTo(8);
        await h.Vm.LoadMoreCommand.Execute();
        await WaitUntilAsync(() => !h.Vm.IsReading, what: "resume after reloading earlier");
        await Assert.That(h.Vm.Rows[^1].Id).IsEqualTo("item-9");
        await h.Dispose();
    });

    static Func<PullRequestSubjectDto, CancellationToken, Task<PullRequestRead<PullRequestOverviewDto>>> Outstanding(
            Harness h, bool? mergeable, int? unresolved, string kind = "exact", string rollup = "success", string lifecycle = "open") =>
        (subject, _) => {
            var ready = h.Source.Overview(subject);
            return Task.FromResult(ready with { Data = ready.Data! with {
                Lifecycle = lifecycle, Mergeable = mergeable,
                Checks = ready.Data.Checks! with { Rollup = rollup },
                Threads = unresolved is null ? null : new() {
                    Availability = new() { Status = "ready", FetchedAt = h.Time.GetUtcNow().UtcDateTime },
                    Unresolved = new() { Value = unresolved, Kind = kind },
                },
            } });
        };

    /// The card counts the threads still unresolved, and offers Fix only while something keeps an open pull
    /// request from merging; Fix hands the composer a prompt naming each of those things.
    [Test]
    public Task Outstanding_work_is_counted_and_fix_prompts_for_each_part() => RunOnUiAsync(async () => {
        var h = new Harness();
        try {
            h.Source.OverviewResponses.Enqueue(Outstanding(h, mergeable: false, unresolved: 2, rollup: "failure"));
            h.Push(); await h.Show();

            await Assert.That(h.Vm.ThreadsStatus.Text).IsEqualTo("2 unresolved threads");
            await Assert.That(h.Vm.ShowsFix).IsTrue();
            await h.Vm.FixCommand.Execute();
            await Assert.That(h.Fixes.Single()).IsEqualTo(
                "Get pull request #2 ready to merge: resolve the merge conflicts with the base branch; address the 2 unresolved review threads; fix the failing checks.");
        } finally { await h.Dispose(); }
    });

    /// Conflicts get a status row and a reader tab of their own, read from the overview alone: opening
    /// the tab must not page rows from the server.
    [Test]
    public Task Conflicts_open_their_own_tab_without_paging() => RunOnUiAsync(async () => {
        var h = new Harness();
        try {
            h.Source.OverviewResponses.Enqueue(Outstanding(h, mergeable: false, unresolved: 0));
            h.Push(); await h.Show();
            h.Vm.SetReaderVisible(true);

            await Assert.That(h.Vm.HasConflicts).IsTrue();
            await Assert.That(h.Vm.ConflictsStatus.Text).IsEqualTo("Merge conflicts");
            await Assert.That(h.Vm.LifecycleStatus.Text).IsEqualTo("Open");
            await h.Vm.ShowSectionCommand.Execute("conflicts");

            await Assert.That(h.Vm.IsConflicts).IsTrue();
            await Assert.That(h.Vm.SelectedTabIndex).IsEqualTo(4);
            await Assert.That(h.Vm.ShowsLoading).IsFalse();
            await Assert.That(h.Vm.PageNote).IsEmpty();
            await Assert.That(h.Source.Pages).IsEqualTo(0);
            await Assert.That(h.Vm.FreshnessLabel).IsEqualTo(h.Vm.FetchedLabel);
            await Assert.That(h.Vm.FreshnessLabel).IsNotEmpty();
        } finally { await h.Dispose(); }
    });

    /// Only a mergeable overview reads as clean: an unreported answer must not claim the conflicts are gone.
    [Test]
    [Arguments(true, "This branch has no conflicts with the base branch.")]
    [Arguments(null, "GitHub has not reported whether this branch merges cleanly.")]
    public Task The_conflicts_note_claims_a_clean_branch_only_when_reported(bool? mergeable, string note) => RunOnUiAsync(async () => {
        var h = new Harness();
        try {
            h.Source.OverviewResponses.Enqueue(Outstanding(h, mergeable, unresolved: 0));
            h.Push(); await h.Show();

            await Assert.That(h.Vm.HasConflicts).IsFalse();
            await Assert.That(h.Vm.ConflictsNote).IsEqualTo(note);
        } finally { await h.Dispose(); }
    });

    /// Fix follows whether the composer can send, such as a session ending under it: the prompt would
    /// otherwise land in a composer that is hidden or refuses it.
    [Test]
    public Task Fix_is_hidden_once_the_composer_cannot_send() => RunOnUiAsync(async () => {
        var h = new Harness();
        try {
            h.Source.OverviewResponses.Enqueue(Outstanding(h, mergeable: false, unresolved: 1));
            h.Push(); await h.Show();
            await Assert.That(h.Vm.ShowsFix).IsTrue();
            var raised = new List<string?>();
            h.Vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            h.CanFix = false;
            h.Vm.FixAvailabilityChanged();

            await Assert.That(raised).Contains(nameof(PullRequestContextViewModel.ShowsFix));
            await Assert.That(h.Vm.ShowsFix).IsFalse();
            await h.Vm.FixCommand.Execute();
            await Assert.That(h.Fixes).IsEmpty();
        } finally { await h.Dispose(); }
    });

    [Test]
    public async Task Fix_keeps_an_unsent_draft_ahead_of_its_prompt() {
        await Assert.That(PullRequestContextViewModel.WithFixPrompt("  ", "Fix it.")).IsEqualTo("Fix it.");
        await Assert.That(PullRequestContextViewModel.WithFixPrompt("my draft\n", "Fix it.")).IsEqualTo("my draft\n\nFix it.");
        await Assert.That(PullRequestContextViewModel.WithFixPrompt("my draft\n\nFix it.", "Fix it.")).IsEqualTo("my draft\n\nFix it.");
    }

    [Test]
    [Arguments(true, 0, "exact", "success", "open", "No unresolved threads", false)]
    [Arguments(null, null, "exact", "success", "open", "", false)]
    [Arguments(true, 100, "lower_bound", "success", "open", "100+ unresolved threads", true)]
    [Arguments(true, 0, "lower_bound", "success", "open", "", false)]
    [Arguments(null, 1, "exact", "success", "draft", "1 unresolved thread", true)]
    public Task Fix_shows_only_for_an_open_pull_request_with_outstanding_work(
            bool? mergeable, int? unresolved, string kind, string rollup, string lifecycle, string threads, bool fix) => RunOnUiAsync(async () => {
        var h = new Harness();
        try {
            h.Source.OverviewResponses.Enqueue(Outstanding(h, mergeable, unresolved, kind, rollup, lifecycle));
            h.Push(); await h.Show();

            await Assert.That(h.Vm.ThreadsStatus.Text).IsEqualTo(threads);
            await Assert.That(h.Vm.ShowsFix).IsEqualTo(fix);
        } finally { await h.Dispose(); }
    });

    [Test]
    public Task The_title_opens_the_overview_tab_even_after_another_section() => RunOnUiAsync(async () => {
        var opened = 0;
        var h = new Harness(onOpen: () => opened++);
        h.Push(); await h.Show();
        await h.Vm.ShowSectionCommand.Execute("checks");
        await WaitUntilAsync(() => h.Vm.IsChecks, what: "checks section open");
        await Assert.That(opened).IsEqualTo(1);
        await h.Vm.OpenReaderCommand.Execute();
        await Assert.That(h.Vm.IsOverview).IsTrue();
        await Assert.That(h.Vm.SelectedTabIndex).IsEqualTo(0);
        await Assert.That(opened).IsEqualTo(2);
        await h.Dispose();
    });

    [Test]
    public Task Cached_checks_keep_their_verdict_while_overview_renewal_is_paused() => RunOnUiAsync(async () => {
        var h = new Harness(); h.Source.TotalPages = 1; h.Push(); await h.Show(); h.Vm.SetReaderVisible(true);
        await h.Vm.ShowSectionCommand.Execute("checks");
        await WaitUntilAsync(() => !h.Vm.IsReading, what: "checks loaded");
        await Assert.That(h.Vm.ChecksStatus.IsDanger).IsTrue();
        h.Source.Failure = "transient";
        h.Time.Advance(TimeSpan.FromSeconds(30));
        await WaitUntilAsync(() => h.Source.Overviews == 2 && !h.Vm.IsReading, what: "renewal paused");
        await Assert.That(h.Vm.HasStaleOverview).IsTrue();
        await Assert.That(h.Vm.ChecksStatus.IsDanger).IsTrue();
        await h.Dispose();
    });

    [Test]
    public Task Complete_current_checks_take_precedence_over_a_conflicting_advisory_rollup() => RunOnUiAsync(async () => {
        var h = new Harness(); h.Source.TotalPages = 1; h.Push(); await h.Show(); h.Vm.SetReaderVisible(true);
        await Assert.That(h.Vm.ChecksStatus.Tip).Contains("All checks have passed");
        await h.Vm.ShowSectionCommand.Execute("checks");
        await WaitUntilAsync(() => h.Vm.Rows.Count == 1, what: "checks page");
        await Assert.That(h.Vm.ChecksStatus.Tip).Contains("1 failed");
        await Assert.That(h.Vm.ChecksStatus.Tip).DoesNotContain("successful");
        await h.Dispose();
    });

    [Test]
    public Task Retry_refreshes_the_current_section_after_access_recovers() => RunOnUiAsync(async () => {
        var h = new Harness(); h.Push(); await h.Show(); h.Vm.SetReaderVisible(true);
        await h.Vm.ShowSectionCommand.Execute("reviews");
        await WaitUntilAsync(() => !h.Vm.IsReading, what: "initial reviews");
        var pages = h.Source.Pages;
        h.Source.Failure = "transient"; h.Time.Advance(TimeSpan.FromSeconds(16));
        await h.Vm.RefreshCommand.Execute();
        await WaitUntilAsync(() => !h.Vm.IsReading, what: "failed renewal");
        await Assert.That(h.Vm.CanReveal).IsFalse();
        h.Source.Failure = null; h.Time.Advance(TimeSpan.FromSeconds(16));
        await h.Vm.RefreshCommand.Execute();
        await WaitUntilAsync(() => !h.Vm.IsReading, what: "recovered renewal");
        await Assert.That(h.Vm.CanReveal).IsTrue();
        await Assert.That(h.Source.Pages).IsEqualTo(pages + 1);
        await h.Dispose();
    });

    [Test]
    public Task A_late_primary_repository_hint_corrects_the_default_without_changing_an_explicit_choice() => RunOnUiAsync(async () => {
        PullRequestRepository? primary = null;
        var h = new Harness(() => primary);
        h.Source.Links[0] = h.Source.Links[0] with { RepoHash = "primary" };
        h.Push(); await h.Show();
        await Assert.That(h.Vm.Selected!.Subject.Number).IsEqualTo(2);
        primary = new("github", "github.com", "example", "repo", "primary");
        h.Time.Advance(TimeSpan.FromSeconds(16)); await h.Vm.RefreshCommand.Execute();
        await WaitUntilAsync(() => !h.Vm.IsReading, what: "primary repository selected");
        await Assert.That(h.Vm.Selected!.Subject.Number).IsEqualTo(1);
        h.Vm.Selected = h.Vm.Choices[1];
        await WaitUntilAsync(() => !h.Vm.IsReading, what: "explicit selection admitted");
        h.Time.Advance(TimeSpan.FromSeconds(16)); await h.Vm.RefreshCommand.Execute();
        await WaitUntilAsync(() => !h.Vm.IsReading, what: "explicit selection retained");
        await Assert.That(h.Vm.Selected!.Subject.Number).IsEqualTo(2);
        await h.Dispose();
    });

    [Test]
    public Task The_default_is_the_newest_unmerged_pull_request() => RunOnUiAsync(async () => {
        var h = new Harness();
        h.Source.Links = [Link(1, "open"), Link(2, "open"), Link(3, "merged")];
        h.Push(); await h.Show();
        await Assert.That(h.Vm.Selected!.Subject.Number).IsEqualTo(2);
        await h.Dispose();
    });

    /// The server's list carries no lifecycle, so the newest PR is tried first and its overview
    /// decides: a merged one hands the default to the next candidate, and a later list refresh
    /// remembers what it learned.
    [Test]
    public Task A_default_whose_overview_says_merged_advances_to_the_next_candidate() => RunOnUiAsync(async () => {
        var h = new Harness();
        h.Source.Links = [Link(1), Link(2), Link(3)];
        h.Source.Lifecycles[3] = "merged";
        h.Push(); h.Vm.SetForeground(true);
        await WaitUntilAsync(() => h.Vm.Selected?.Subject.Number == 2 && h.Vm.CanReveal, what: "merged default advanced");
        await Assert.That(h.Source.Overviews).IsEqualTo(2);
        h.Time.Advance(TimeSpan.FromSeconds(16)); await h.Vm.RefreshCommand.Execute();
        await WaitUntilAsync(() => !h.Vm.IsReading, what: "list refresh");
        await Assert.That(h.Vm.Selected!.Subject.Number).IsEqualTo(2);
        await h.Dispose();
    });

    [Test]
    public Task An_explicit_selection_stays_when_its_overview_says_merged() => RunOnUiAsync(async () => {
        var h = new Harness();
        h.Source.Lifecycles[2] = "merged";
        h.Push(); h.Vm.SetForeground(true);
        await WaitUntilAsync(() => h.Vm.Selected?.Subject.Number == 1 && h.Vm.CanReveal, what: "merged default advanced");
        h.Vm.Selected = h.Vm.Choices.Single(choice => choice.Subject.Number == 2);
        await WaitUntilAsync(() => h.Vm.CanReveal, what: "explicit merged PR admitted");
        h.Time.Advance(TimeSpan.FromSeconds(16)); await h.Vm.RefreshCommand.Execute();
        await WaitUntilAsync(() => !h.Vm.IsReading, what: "list refresh");
        await Assert.That(h.Vm.Selected!.Subject.Number).IsEqualTo(2);
        await h.Dispose();
    });

    /// A live list row says open until the list's next refresh; the overview read in between is fresher.
    [Test]
    public Task A_learned_lifecycle_outranks_the_list_row_it_came_with() => RunOnUiAsync(async () => {
        var h = new Harness();
        h.Source.Links = [Link(1, "open"), Link(2, "open")];
        h.Source.Lifecycles[2] = "merged";
        h.Push(); h.Vm.SetForeground(true);
        await WaitUntilAsync(() => h.Vm.Selected?.Subject.Number == 1 && h.Vm.CanReveal, what: "merged default advanced past its open list row");
        await h.Dispose();
    });

    /// The overview is re-read while the card is shown; a PR that merges meanwhile hands the
    /// default over on that read, without waiting for a list refresh.
    [Test]
    public Task A_default_that_merges_while_shown_hands_over_on_its_next_overview() => RunOnUiAsync(async () => {
        var h = new Harness(); h.Push(); await h.Show();
        // Settling the read arms its renewal timer after the overview becomes visible.
        await WaitUntilAsync(() => !h.Vm.IsReading, what: "initial overview settled");
        await Assert.That(h.Vm.Selected!.Subject.Number).IsEqualTo(2);
        h.Source.Lifecycles[2] = "merged";
        h.Time.Advance(TimeSpan.FromSeconds(26)); Dispatcher.UIThread.RunJobs();
        await WaitUntilAsync(() => h.Vm.Selected?.Subject.Number == 1 && h.Vm.CanReveal, what: "merged default handed over");
        await Assert.That(h.Source.Lists).IsEqualTo(1);
        await h.Dispose();
    });

    /// Among unmerged PRs in the primary repository the checked-out branch's PR outranks a newer one.
    [Test]
    public Task The_branch_pull_request_outranks_a_newer_one_in_the_same_repository() => RunOnUiAsync(async () => {
        var primary = new PullRequestRepository("github", "github.com", "example", "repo", "hash");
        var h = new Harness(() => primary);
        h.Source.Links = [Link(1, "open"), Link(2, "open") with { HeadRef = "other" }];
        h.Push(); await h.Show();
        await Assert.That(h.Vm.Selected!.Subject.Number).IsEqualTo(1);
        await h.Dispose();
    });

    [Test]
    public Task An_unlinked_explicit_selection_stays_unavailable_until_it_returns_or_the_user_selects_another_PR() => RunOnUiAsync(async () => {
        var h = new Harness(); h.Push(); await h.Show(); h.Vm.SetReaderVisible(true);
        h.Vm.Selected = h.Vm.Choices[0];
        await WaitUntilAsync(() => h.Vm.CanReveal, what: "explicit PR admitted");
        var removed = h.Source.Links[0];
        h.Source.Links = [h.Source.Links[1]];
        var overviews = h.Source.Overviews;
        for (var refresh = 0; refresh < 2; refresh++) {
            h.Time.Advance(TimeSpan.FromSeconds(16)); await h.Vm.RefreshCommand.Execute();
            await WaitUntilAsync(() => !h.Vm.IsReading, what: "missing selection applied");
            await Assert.That(h.Vm.Selected!.Subject.Number).IsEqualTo(1);
            await Assert.That(h.Vm.Selected.IsAvailable).IsFalse();
            await Assert.That(h.Vm.Description).IsNull();
            await Assert.That(h.Vm.CanReveal).IsFalse();
            await Assert.That(h.Source.Overviews).IsEqualTo(overviews);
        }
        await h.Vm.OpenGitHubCommand.Execute();
        await Assert.That(h.Opener.Opened).IsEmpty();
        h.Source.Links = [removed, h.Source.Links[0]];
        h.Time.Advance(TimeSpan.FromSeconds(16)); await h.Vm.RefreshCommand.Execute();
        await WaitUntilAsync(() => h.Vm.CanReveal, what: "relinked selection admitted");
        await Assert.That(h.Vm.Selected!.Subject.Number).IsEqualTo(1);
        await Assert.That(h.Vm.Selected.IsAvailable).IsTrue();
        h.Source.Links = [h.Source.Links[1]];
        h.Time.Advance(TimeSpan.FromSeconds(16)); await h.Vm.RefreshCommand.Execute();
        await WaitUntilAsync(() => !h.Vm.IsReading, what: "selection removed again");
        h.Vm.Selected = h.Vm.Choices.Single(choice => choice.Subject.Number == 2);
        await WaitUntilAsync(() => h.Vm.CanReveal, what: "replacement explicitly selected");
        await Assert.That(h.Vm.Selected!.Subject.Number).IsEqualTo(2);
        await h.Dispose();
    });

    [Test]
    public Task An_unlinked_explicit_selection_does_not_count_as_a_linked_PR() => RunOnUiAsync(async () => {
        var h = new Harness(); h.Push(); await h.Show();
        h.Vm.Selected = h.Vm.Choices[0];
        await WaitUntilAsync(() => h.Vm.CanReveal, what: "explicit PR admitted");
        h.Source.Links = [];
        h.Time.Advance(TimeSpan.FromSeconds(16)); await h.Vm.RefreshCommand.Execute();
        await WaitUntilAsync(() => !h.Vm.IsReading, what: "unlink applied");
        await Assert.That(h.Vm.Selected!.IsAvailable).IsFalse();
        await Assert.That(h.Vm.HasPullRequest).IsFalse();
        await h.Dispose();
    });

    [Test]
    public Task A_signed_out_discovery_forgets_the_linked_PRs_until_discovery_succeeds_again() => RunOnUiAsync(async () => {
        var h = new Harness(); h.Push(); await h.Show();
        h.Source.Capability = PullRequestCapabilityKind.SignedOut;
        h.Time.Advance(TimeSpan.FromSeconds(16)); await h.Vm.RefreshCommand.Execute();
        await WaitUntilAsync(() => !h.Vm.IsReading, what: "signed-out discovery applied");
        await Assert.That(h.Vm.HasPullRequest).IsFalse();
        await Assert.That(h.Vm.ShowsSignIn).IsTrue();
        h.Source.Capability = PullRequestCapabilityKind.Supported;
        h.Time.Advance(TimeSpan.FromSeconds(16)); await h.Vm.RefreshCommand.Execute();
        await WaitUntilAsync(() => h.Vm.HasPullRequest && !h.Vm.IsReading, what: "PRs rediscovered");
        await h.Dispose();
    });

    /// The reader draws a mailto because the policy accepts it, so the body command opens that same URL.
    [Test]
    public Task A_body_mailto_opens_and_a_script_link_does_not() => RunOnUiAsync(async () => {
        var h = new Harness();
        h.Push();
        await h.Show();
        h.Vm.SetReaderVisible(true);
        await h.Vm.OpenBodyLinkCommand.Execute("mailto:test@example.com");
        await h.Vm.OpenBodyLinkCommand.Execute("javascript:alert(1)");
        await Assert.That(h.Opener.Opened).IsEquivalentTo(new[] { "mailto:test@example.com" });
        await h.Dispose();
    });

    [Test]
    [Arguments("https://example.com/example/repo/pull/1", false)]
    [Arguments("https://github.com/example/repo/pull/99", false)]
    [Arguments("https://github.com/example/repo/issues/1", false)]
    [Arguments("https://github.com/example/repo/pull/1", true)]
    [Arguments("https://github.com/example/renamed/pull/1", true)]
    public Task The_main_GitHub_action_opens_only_the_selected_PR(string url, bool accepted) => RunOnUiAsync(async () => {
        var h = new Harness();
        h.Source.Links = [h.Source.Links[0] with { Url = url }];
        h.Push(); await h.Show();
        await h.Vm.OpenGitHubCommand.Execute();
        await Assert.That(h.Opener.Opened.Count).IsEqualTo(accepted ? 1 : 0);
        if (accepted) await Assert.That(h.Opener.Opened[0]).IsEqualTo(url);
        await h.Dispose();
    });

    /// A borrowed reviewer's snapshot is detached. Discovery has to read the checkout being reviewed.
    [Test]
    public Task A_borrowed_reviewer_opens_the_pull_request_on_the_reviewed_checkout() => RunOnUiAsync(async () => {
        using var tmp = new TempDir();
        var reviewed = tmp.CreateDir("reviewed");
        var snapshot = tmp.CreateDir("snapshot");
        tmp.CreateFile(["reviewed", ".git", "HEAD"], "ref: refs/heads/reviewed\n");
        tmp.CreateFile(["snapshot", ".git", "HEAD"], new string('a', 40) + "\n");
        var h = new Harness(() => new PullRequestRepository("github", "github.com", "example", "repo", "hash"));
        h.Source.Links = [
            FakePullRequestSource.Link(1) with { HeadRef = "other" },
            FakePullRequestSource.Link(2) with { HeadRef = "reviewed" },
        ];
        h.Presence.OnNext(Agent("agent", "claude", hasTerminal: false, sessionId: "session",
            worktreePath: snapshot, workLocation: "borrowed", borrowedFrom: reviewed));
        await h.Show();
        await Assert.That(h.Vm.Selected!.Subject.Number).IsEqualTo(2);
        await h.Dispose();
    });

    static PullRequestLinkDto Link(int number, string? lifecycle = null) => FakePullRequestSource.Link(number) with { Lifecycle = lifecycle };

    sealed class Harness {
        internal BehaviorSubject<AgentStatusDto?> Presence { get; } = new(null);
        internal FakeTimeProvider Time { get; } = new();
        internal FakePullRequestSource Source { get; }
        internal RecordingOpener Opener { get; } = new();
        internal PullRequestContextViewModel Vm { get; }
        internal TimerCountingTimeProvider Clock { get; }
        internal Subject<System.Reactive.Unit> SignIn { get; } = new();
        internal readonly List<string> Fixes = [];
        internal bool CanFix = true;
        internal Harness(Func<PullRequestRepository?>? primary = null, Action? onOpen = null, Func<string?, string?>? readBranch = null,
                LastReads<PullRequestSnapshot>? snapshots = null, Action<string>? linksChanged = null) {
            Source = new(Time);
            Clock = new(Time);
            Vm = new(Presence, Source, Clock, Opener, onOpen ?? (() => { }), primaryRepo: primary, signInCompleted: SignIn, readBranch: readBranch,
                snapshots: snapshots, linksChanged: linksChanged, requestFix: Fixes.Add, canRequestFix: () => CanFix);
        }
        internal void Push() => Presence.OnNext(Agent("agent", "claude", hasTerminal: false, sessionId: "session", branch: "feature"));
        /// Returns once the initial reads have settled, not just once the overview is admitted: a read's settle
        /// job re-arms the tick after the job that reveals it, and a clock advance made before that re-arm fires nothing.
        internal async Task Show() {
            Vm.SetForeground(true);
            await WaitUntilAsync(() => Vm.CanReveal && !Vm.IsReading, what: "PR overview admitted and settled");
        }
        internal async Task Dispose() { await Vm.TeardownAsync(); Presence.Dispose(); SignIn.Dispose(); }
    }

    sealed class TimerCountingTimeProvider(TimeProvider inner) : TimeProvider {
        internal int Callbacks { get; private set; }
        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();
        public override long GetTimestamp() => inner.GetTimestamp();
        public override long TimestampFrequency => inner.TimestampFrequency;
        public override TimeZoneInfo LocalTimeZone => inner.LocalTimeZone;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            inner.CreateTimer(value => { Callbacks++; callback(value); }, state, dueTime, period);
    }
}
