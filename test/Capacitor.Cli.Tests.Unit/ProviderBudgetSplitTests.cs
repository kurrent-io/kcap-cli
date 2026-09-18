using Capacitor.Cli.PrDetection;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// The probe and the PR/MR detector share one ceiling: the detector runs within what the probe LEFT
/// BEHIND, not the full cap. Real time decides that split in production, so the timestamp is
/// controlled here — otherwise handing the detector the full cap reads as a fast machine.
/// </summary>
public class ProviderBudgetSplitTests {
    [Test]
    public async Task Detector_gets_the_budget_the_probe_left_behind() {
        var providerCap = TimeSpan.FromSeconds(2);

        var time = new FakeTimeProvider();

        TimeSpan? detectorCap = null;
        CommandRunner run = (cmd, _, _, cap) => {
            if (cmd == "glab") { detectorCap = cap; return Task.FromResult<string?>("[]"); }
            time.Advance(TimeSpan.FromSeconds(1)); // the probe spends half the cap
            return Task.FromResult<string?>("{}"); // gh auth status: custom host not a gh host → GitLab
        };

        // Custom host → the router probes (consuming the injected time); GitLab detector then runs.
        await RepositoryDetection.ResolveAndDetectPrAsync(
            new GitProviderRouter(),
            "git.example.com", "owner", "repo", "main", "/cwd", providerCap, run, time);

        await Assert.That(detectorCap).IsNotNull();
        await Assert.That(detectorCap!.Value).IsLessThan(providerCap);                 // NOT the full cap
        await Assert.That(detectorCap!.Value.TotalMilliseconds).IsGreaterThan(500);    // ≈ 1s remainder
        await Assert.That(detectorCap!.Value.TotalMilliseconds).IsLessThan(1500);
    }

    [Test]
    public async Task No_detection_when_probe_exhausts_the_budget() {
        var providerCap = TimeSpan.FromSeconds(2);

        var time = new FakeTimeProvider();

        var detectorRan = false;
        CommandRunner run = (cmd, _, _, _) => {
            if (cmd == "glab") detectorRan = true;
            else time.Advance(providerCap); // the probe spends the whole cap
            return Task.FromResult<string?>(cmd == "glab" ? "[]" : "{}");
        };

        var pr = await RepositoryDetection.ResolveAndDetectPrAsync(
            new GitProviderRouter(),
            "git.example.com", "owner", "repo", "main", "/cwd", providerCap, run, time);

        await Assert.That(detectorRan).IsFalse();
        await Assert.That(pr).IsNull();
    }

    /// <summary>A branch tracking <c>remote-name</c> whose normal <c>gh pr view</c> misses after
    /// advancing the clock by <paramref name="normalLookupCost"/>.</summary>
    static CommandRunner TrackedBranchRunner(
            FakeTimeProvider time, TimeSpan normalLookupCost, List<(string Args, TimeSpan Cap)> calls) =>
        (cmd, args, _, cap) => {
            calls.Add(($"{cmd} {args}", cap));

            if (cmd == "gh" && args.StartsWith("pr view --json", StringComparison.Ordinal)) {
                time.Advance(normalLookupCost);
                return Task.FromResult<string?>(null);
            }

            string? reply = args switch {
                "config --get branch.local-name.remote" => "origin",
                "config --get branch.local-name.merge"  => "refs/heads/remote-name",
                "remote get-url origin"                 => "git@github.com:acme/widget.git",
                _ when args.StartsWith("symbolic-ref ", StringComparison.Ordinal) => "refs/remotes/origin/main",
                _ => null
            };

            return Task.FromResult(reply);
        };

    [Test]
    public async Task Tracked_branch_lookup_gets_only_what_the_normal_lookup_left() {
        var time  = new FakeTimeProvider();
        var calls = new List<(string Args, TimeSpan Cap)>();
        var run   = TrackedBranchRunner(time, TimeSpan.FromSeconds(1.5), calls);

        await RepositoryDetection.ResolveAndDetectPrAsync(
            new GitProviderRouter(),
            "github.com", "acme", "widget", "local-name", "/cwd", TimeSpan.FromSeconds(2), run, time);

        var tracked = calls.Single(c => c.Args.StartsWith("gh pr view remote-name", StringComparison.Ordinal));
        await Assert.That(tracked.Cap).IsGreaterThan(TimeSpan.Zero);
        await Assert.That(tracked.Cap).IsLessThanOrEqualTo(TimeSpan.FromMilliseconds(500));
    }

    [Test]
    public async Task No_tracked_branch_probe_once_the_normal_lookup_spends_the_budget() {
        var time  = new FakeTimeProvider();
        var calls = new List<(string Args, TimeSpan Cap)>();
        var run   = TrackedBranchRunner(time, TimeSpan.FromSeconds(2), calls);

        var pr = await RepositoryDetection.ResolveAndDetectPrAsync(
            new GitProviderRouter(),
            "github.com", "acme", "widget", "local-name", "/cwd", TimeSpan.FromSeconds(2), run, time);

        await Assert.That(pr).IsNull();
        await Assert.That(calls.Select(c => c.Args)).IsEquivalentTo(["gh pr view --json number,title,url,headRefName"]);
    }
}
