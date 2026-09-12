using System.Diagnostics;
using Capacitor.Cli.PrDetection;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// Guards the effective-provider-budget split added in #229: the probe (GitProviderRouter) and the
/// PR/MR detector share one ceiling, and the detector must run within the budget the probe LEFT
/// BEHIND — not the full cap. This is timing-dependent in production; an injected timestamp makes
/// it deterministic so a regression (handing the detector the full cap) is caught in CI.
/// </summary>
public class ProviderBudgetSplitTests {
    [Before(Test)]
    public void Reset() => GitProviderRouter.ResetMemoForTests();

    [Test]
    public async Task Detector_gets_the_budget_the_probe_left_behind() {
        var providerCap = TimeSpan.FromSeconds(2);

        // The probe "consumes" exactly half the cap: timestamp reads 0 at start, +1s afterwards.
        var calls = 0;
        long Timestamp() => calls++ == 0 ? 0L : Stopwatch.Frequency; // Frequency ticks == 1 second

        TimeSpan? detectorCap = null;
        CommandRunner run = (cmd, _, _, cap) => {
            if (cmd == "glab") { detectorCap = cap; return Task.FromResult<string?>("[]"); }
            return Task.FromResult<string?>("{}"); // gh auth status: custom host not a gh host → GitLab
        };

        // Custom host → the router probes (consuming the injected time); GitLab detector then runs.
        await RepositoryDetection.ResolveAndDetectPrAsync(
            "git.example.com", "owner", "repo", "main", "/cwd", providerCap, run, Timestamp);

        await Assert.That(detectorCap).IsNotNull();
        await Assert.That(detectorCap!.Value).IsLessThan(providerCap);                 // NOT the full cap
        await Assert.That(detectorCap!.Value.TotalMilliseconds).IsGreaterThan(500);    // ≈ 1s remainder
        await Assert.That(detectorCap!.Value.TotalMilliseconds).IsLessThan(1500);
    }

    [Test]
    public async Task No_detection_when_probe_exhausts_the_budget() {
        var providerCap = TimeSpan.FromSeconds(2);

        // Probe consumes the whole cap (0 → 2s) → no budget left → detector must not run.
        var calls = 0;
        long Timestamp() => calls++ == 0 ? 0L : Stopwatch.Frequency * 2;

        var detectorRan = false;
        CommandRunner run = (cmd, _, _, _) => {
            if (cmd == "glab") detectorRan = true;
            return Task.FromResult<string?>(cmd == "glab" ? "[]" : "{}");
        };

        var pr = await RepositoryDetection.ResolveAndDetectPrAsync(
            "git.example.com", "owner", "repo", "main", "/cwd", providerCap, run, Timestamp);

        await Assert.That(detectorRan).IsFalse();
        await Assert.That(pr).IsNull();
    }

    [Test]
    public async Task Non_monotonic_timestamp_never_inflates_the_detector_budget() {
        var providerCap = TimeSpan.FromSeconds(2);

        // A misbehaving timestamp seam that goes BACKWARDS (end < start) would make GetElapsedTime
        // negative and, unclamped, push detectCap above providerCap. The clamp must keep the
        // detector budget within the shared ceiling.
        var calls = 0;
        long Timestamp() => calls++ == 0 ? Stopwatch.Frequency : 0L; // start high, end low → negative elapsed

        TimeSpan? detectorCap = null;
        CommandRunner run = (cmd, _, _, cap) => {
            if (cmd == "glab") { detectorCap = cap; return Task.FromResult<string?>("[]"); }
            return Task.FromResult<string?>("{}");
        };

        await RepositoryDetection.ResolveAndDetectPrAsync(
            "git.example.com", "owner", "repo", "main", "/cwd", providerCap, run, Timestamp);

        await Assert.That(detectorCap).IsNotNull();
        await Assert.That(detectorCap!.Value).IsLessThanOrEqualTo(providerCap); // never exceeds the ceiling
    }

    /// <summary>A branch tracking <c>remote-name</c> whose normal <c>gh pr view</c> misses after
    /// advancing the injected clock by <paramref name="normalLookupCost"/>.</summary>
    static CommandRunner TrackedBranchRunner(
            Func<long> now, Action<long> setNow, TimeSpan normalLookupCost, List<(string Args, TimeSpan Cap)> calls) =>
        (cmd, args, _, cap) => {
            calls.Add(($"{cmd} {args}", cap));

            if (cmd == "gh" && args.StartsWith("pr view --json", StringComparison.Ordinal)) {
                setNow(now() + (long)(normalLookupCost.TotalSeconds * Stopwatch.Frequency));
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
        long now  = 0;
        var calls = new List<(string Args, TimeSpan Cap)>();
        var run   = TrackedBranchRunner(() => now, t => now = t, TimeSpan.FromSeconds(1.5), calls);

        await RepositoryDetection.ResolveAndDetectPrAsync(
            "github.com", "acme", "widget", "local-name", "/cwd", TimeSpan.FromSeconds(2), run, () => now);

        var tracked = calls.Single(c => c.Args.StartsWith("gh pr view remote-name", StringComparison.Ordinal));
        await Assert.That(tracked.Cap).IsGreaterThan(TimeSpan.Zero);
        await Assert.That(tracked.Cap).IsLessThanOrEqualTo(TimeSpan.FromMilliseconds(500));
    }

    [Test]
    public async Task No_tracked_branch_probe_once_the_normal_lookup_spends_the_budget() {
        long now  = 0;
        var calls = new List<(string Args, TimeSpan Cap)>();
        var run   = TrackedBranchRunner(() => now, t => now = t, TimeSpan.FromSeconds(2), calls);

        var pr = await RepositoryDetection.ResolveAndDetectPrAsync(
            "github.com", "acme", "widget", "local-name", "/cwd", TimeSpan.FromSeconds(2), run, () => now);

        await Assert.That(pr).IsNull();
        await Assert.That(calls.Select(c => c.Args)).IsEquivalentTo(["gh pr view --json number,title,url,headRefName"]);
    }
}
