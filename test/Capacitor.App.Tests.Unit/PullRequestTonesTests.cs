using Capacitor.App.ViewModels;
using Capacitor.Cli.Core.PullRequests;

namespace Capacitor.App.Tests.Unit;

public class PullRequestTonesTests {
    static PullRequestOverviewDto Overview(string? lifecycle = "open", string? rollup = null, bool? draft = null, bool? mergeable = null) => new() {
        Lifecycle = lifecycle, IsDraft = draft, Mergeable = mergeable,
        Checks = rollup is null ? null : new() { Availability = new() { Status = "ready" }, Rollup = rollup },
    };

    [Test]
    [Arguments("merged", null, null, null, PullRequestTone.Merged)]
    [Arguments("closed", null, null, null, PullRequestTone.Closed)]
    [Arguments("open", "failure", null, null, PullRequestTone.ChecksFailed)]
    [Arguments("open", null, null, false, PullRequestTone.Conflict)]
    [Arguments("open", "pending", null, null, PullRequestTone.ChecksRunning)]
    [Arguments("draft", null, null, null, PullRequestTone.Draft)]
    [Arguments("open", null, true, null, PullRequestTone.Draft)]
    [Arguments("open", "success", null, null, PullRequestTone.Ready)]
    [Arguments("open", null, null, null, PullRequestTone.Ready)]
    [Arguments("open", "success", false, true, PullRequestTone.Ready)]
    [Arguments(null, null, null, null, PullRequestTone.None)]
    public async Task An_overview_maps_to_one_tone(string? lifecycle, string? rollup, bool? draft, bool? mergeable, PullRequestTone tone) {
        await Assert.That(PullRequestTones.From(Overview(lifecycle, rollup, draft, mergeable))).IsEqualTo(tone);
    }

    /// A closed or merged PR is past its checks; among live ones a failure outranks a conflict,
    /// which outranks a run in progress, which outranks the draft marker.
    [Test]
    [Arguments("closed", "failure", null, false, PullRequestTone.Closed)]
    [Arguments("merged", "pending", true, null, PullRequestTone.Merged)]
    [Arguments("open", "failure", null, false, PullRequestTone.ChecksFailed)]
    [Arguments("open", "pending", null, false, PullRequestTone.Conflict)]
    [Arguments("draft", "pending", null, null, PullRequestTone.ChecksRunning)]
    [Arguments("draft", "failure", null, null, PullRequestTone.ChecksFailed)]
    public async Task Precedence_picks_the_most_actionable_state(string? lifecycle, string? rollup, bool? draft, bool? mergeable, PullRequestTone tone) {
        await Assert.That(PullRequestTones.From(Overview(lifecycle, rollup, draft, mergeable))).IsEqualTo(tone);
    }

    [Test]
    public async Task Strongest_prefers_the_tone_that_needs_attention() {
        await Assert.That(PullRequestTones.Strongest([])).IsEqualTo(PullRequestTone.None);
        await Assert.That(PullRequestTones.Strongest([PullRequestTone.Merged, PullRequestTone.Ready])).IsEqualTo(PullRequestTone.Ready);
        await Assert.That(PullRequestTones.Strongest([PullRequestTone.Ready, PullRequestTone.ChecksFailed])).IsEqualTo(PullRequestTone.ChecksFailed);
        await Assert.That(PullRequestTones.Strongest([PullRequestTone.Draft, PullRequestTone.ChecksRunning])).IsEqualTo(PullRequestTone.ChecksRunning);
        await Assert.That(PullRequestTones.Strongest([PullRequestTone.Closed, PullRequestTone.None])).IsEqualTo(PullRequestTone.Closed);
    }

    [Test]
    [Arguments(PullRequestTone.Ready, "Ready to merge")]
    [Arguments(PullRequestTone.Draft, "Draft")]
    [Arguments(PullRequestTone.ChecksRunning, "Checks running")]
    [Arguments(PullRequestTone.ChecksFailed, "Checks failed")]
    [Arguments(PullRequestTone.Conflict, "Merge conflicts")]
    [Arguments(PullRequestTone.Merged, "Merged")]
    [Arguments(PullRequestTone.Closed, "Closed")]
    [Arguments(PullRequestTone.None, "")]
    public async Task Each_tone_has_a_label(PullRequestTone tone, string label) {
        await Assert.That(PullRequestTones.Label(tone)).IsEqualTo(label);
    }

    /// The card's lifecycle label shares the tone vocabulary: a conflicting open PR says so in
    /// the warning colour, and a draft reads muted rather than green.
    [Test]
    public async Task The_lifecycle_status_names_conflicts_and_drafts() {
        var conflict = PullRequestTones.LifecycleStatus(Overview("open", mergeable: false));
        await Assert.That(conflict.Text).IsEqualTo("Merge conflicts");
        await Assert.That(conflict.IsWarning).IsTrue();

        var draft = PullRequestTones.LifecycleStatus(Overview("draft"));
        await Assert.That(draft.Text).IsEqualTo("Draft");
        await Assert.That(draft.IsMuted).IsTrue();
        await Assert.That(draft.IsSuccess).IsFalse();

        var open = PullRequestTones.LifecycleStatus(Overview("open"));
        await Assert.That(open.Text).IsEqualTo("Open");
        await Assert.That(open.IsSuccess).IsTrue();

        await Assert.That(PullRequestTones.LifecycleStatus(Overview("merged")).IsPurple).IsTrue();
        await Assert.That(PullRequestTones.LifecycleStatus(Overview("closed")).IsDanger).IsTrue();
        await Assert.That(PullRequestTones.LifecycleStatus(null).Text).IsEqualTo("Unknown");
    }
}
