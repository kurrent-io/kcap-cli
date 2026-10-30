using Capacitor.App.ViewModels;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Accounts.Readings;

namespace Capacitor.App.Tests.Unit;

public class AccountUsageTests {
    static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
    static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    static UsageWindowDto W(string limit, int minutes, double used, DateTimeOffset? resets = null, DateTimeOffset? confirmed = null) =>
        new(limit, minutes, used, resets ?? Now.AddHours(2), confirmed ?? Now.AddMinutes(-3));

    static AccountReadingDto Reading(UsageWindowDto[] windows, BlockedMarkerDto? blocked = null) =>
        new(1, windows, null, null, blocked);

    static BlockedMarkerDto Hit(params BlockConstraintDto[] constraints) =>
        new(Now.AddMinutes(-10), null, constraints);

    static BlockConstraintDto Constraint(string? limit, int? minutes, DateTimeOffset? resets) =>
        new(limit, minutes, resets, Now.AddMinutes(-10));

    static AccountViewDto Account(
        AccountReadingDto? reading,
        string vendor = "claude",
        string state = "recording",
        string? signIn = SignInStates.SignedIn) =>
        new("a1", vendor, "/d", "/d", "home", state, SignIn: signIn, Reading: reading);

    static AccountUsageView Project(AccountReadingDto? reading, string vendor = "claude") =>
        AccountUsage.Project(Account(reading, vendor), Now, Utc);

    [Test]
    public async Task Null_reading_is_no_reading_yet() {
        var view = Project(null);
        await Assert.That(view.LineText).IsEqualTo("no reading yet");
        await Assert.That(view.Bars.Count).IsEqualTo(0);
        await Assert.That(view.Ready).IsFalse();
    }

    [Test]
    public async Task Empty_unblocked_reading_is_no_reading_yet() =>
        await Assert.That(Project(Reading([])).LineText).IsEqualTo("no reading yet");

    [Test]
    public async Task Metadata_only_is_no_reading_yet() {
        var view = Project(new AccountReadingDto(1, [], "max", null, null, Now));
        await Assert.That(view.LineText).IsEqualTo("no reading yet");
        await Assert.That(view.BlockedText).IsNull();
    }

    [Test]
    public async Task Blocked_before_any_window_is_usage_unknown_with_limit_hit() {
        var view = Project(Reading([], Hit(Constraint("claude", 300, null))));
        await Assert.That(view.LineText).IsEqualTo("usage unknown");
        await Assert.That(view.BlockedText).IsEqualTo("Limit hit");
        await Assert.That(view.Bars.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Per_model_only_is_usage_unknown_with_the_count() {
        var view = Project(Reading([W("sonnet", 10080, 20), W("opus", 10080, 30)]));
        await Assert.That(view.LineText).IsEqualTo("usage unknown · +2 per model");
        await Assert.That(view.PerModelText).IsNull();
        await Assert.That(view.Bars.Count).IsEqualTo(0);
        await Assert.That(view.Ready).IsFalse();
    }

    [Test]
    public async Task Per_model_only_with_a_hit_reads_the_hit_in_the_line() {
        var view = Project(Reading([W("sonnet", 10080, 100)], Hit(Constraint("sonnet", 10080, Now.AddDays(2)))));
        await Assert.That(view.LineText).IsEqualTo("usage unknown · +1 per model · 1 hit");
        await Assert.That(view.BlockedText).IsNull();
    }

    [Test]
    public async Task Per_model_only_with_a_live_main_constraint_is_blocked() {
        var view = Project(Reading([W("sonnet", 10080, 20)], Hit(Constraint(null, null, null))));
        await Assert.That(view.LineText).IsEqualTo("usage unknown · +1 per model");
        await Assert.That(view.BlockedText).IsEqualTo("Limit hit");
    }

    [Test]
    public async Task Unmodelled_vendor_has_no_main_limit() {
        var view = Project(Reading([W("claude", 300, 10)]), "gemini");
        await Assert.That(view.Bars.Count).IsEqualTo(0);
        await Assert.That(view.LineText).IsEqualTo("usage unknown · +1 per model");
    }

    [Test]
    public async Task Primary_bars_sort_by_length_and_count_others() {
        var view = Project(Reading([W("claude", 10080, 40), W("claude", 300, 10), W("sonnet", 10080, 5), W("sonnet", 300, 5)]));
        await Assert.That(string.Join(",", view.Bars.Select(b => b.Label))).IsEqualTo("5h,Week");
        await Assert.That(view.PerModelText).IsEqualTo("+1 per model");
        await Assert.That(view.LineText).IsNull();
    }

    [Test]
    public async Task Codex_uses_its_own_main_limit() {
        var view = Project(Reading([W("codex", 300, 10), W("claude", 300, 90)]), "codex");
        await Assert.That(view.Bars.Count).IsEqualTo(1);
        await Assert.That(view.Bars[0].PercentText).IsEqualTo("10%");
    }

    [Test]
    public async Task Warning_from_75_rounded() {
        var below = Project(Reading([W("claude", 300, 74.4)])).Bars[0];
        var from  = Project(Reading([W("claude", 300, 74.6)])).Bars[0];
        await Assert.That(below.PercentText).IsEqualTo("74%");
        await Assert.That(below.Tone).IsEqualTo(UsageTone.Neutral);
        await Assert.That(from.PercentText).IsEqualTo("75%");
        await Assert.That(from.Tone).IsEqualTo(UsageTone.Warning);
    }

    [Test]
    public async Task Hundred_is_danger() =>
        await Assert.That(Project(Reading([W("claude", 300, 100)])).Bars[0].Tone).IsEqualTo(UsageTone.Danger);

    [Test]
    public async Task Out_of_range_percent_is_clamped() {
        var view = Project(Reading([W("claude", 300, 101), W("claude", 10080, -3)]));
        await Assert.That(view.Bars[0].PercentText).IsEqualTo("100%");
        await Assert.That(view.Bars[1].PercentText).IsEqualTo("0%");
    }

    [Test]
    public async Task Confirmed_zero_is_drawn_as_zero() {
        var bar = Project(Reading([W("claude", 300, 0)])).Bars[0];
        await Assert.That(bar.Percent).IsEqualTo(0);
        await Assert.That(bar.PercentText).IsEqualTo("0%");
        await Assert.That(bar.Tone).IsEqualTo(UsageTone.Neutral);
    }

    [Test]
    public async Task Expired_window_reads_reset_with_usage_not_read() {
        var bar = Project(Reading([W("claude", 300, 80, Now.AddMinutes(-1))])).Bars[0];
        await Assert.That(bar.Percent).IsNull();
        await Assert.That(bar.PercentText).IsEqualTo("reset");
        await Assert.That(bar.Tone).IsEqualTo(UsageTone.Unknown);
        await Assert.That(bar.ResetText).IsEqualTo("new window · usage not read yet");
    }

    /// Once the 5h window has reset, the next reset is the week's, and an unlabelled one reads as the session's.
    [Test]
    public async Task Next_reset_after_the_session_window_resets_names_the_week() {
        var view = Project(Reading([W("claude", 300, 80, Now.AddMinutes(-1)), W("claude", 10080, 77, Now.AddDays(4))]));
        await Assert.That(view.NextResetText).IsEqualTo("Week resets Sat 12:00");
    }

    [Test]
    public async Task Blocked_window_is_danger() {
        var view = Project(Reading([W("claude", 300, 50), W("claude", 10080, 50)], Hit(Constraint("claude", 300, Now.AddHours(1)))));
        await Assert.That(view.Bars[0].Tone).IsEqualTo(UsageTone.Danger);
        await Assert.That(view.Bars[1].Tone).IsEqualTo(UsageTone.Neutral);
    }

    [Test]
    public async Task Per_model_hit_does_not_block() {
        var view = Project(Reading([W("claude", 300, 10), W("sonnet", 10080, 100)], Hit(Constraint("sonnet", 10080, Now.AddDays(2)))));
        await Assert.That(view.PerModelText).IsEqualTo("+1 per model · 1 hit");
        await Assert.That(view.BlockedText).IsNull();
        await Assert.That(view.Ready).IsTrue();
    }

    [Test]
    public async Task Null_limit_constraint_blocks() {
        var account = Account(Reading([W("claude", 300, 10)], Hit(Constraint(null, null, null))));
        await Assert.That(AccountUsage.IsBlocked(account, Now)).IsTrue();
        await Assert.That(AccountUsage.Project(account, Now, Utc).Bars[0].Tone).IsEqualTo(UsageTone.Neutral);
    }

    [Test]
    public async Task Blocked_until_is_the_latest_reset_in_either_signal_order() {
        var soon = Constraint("claude", 300, Now.AddHours(1));
        var late = Constraint("claude", 10080, Now.AddHours(5));
        var a = Project(Reading([W("claude", 300, 10)], Hit(soon, late)));
        var b = Project(Reading([W("claude", 300, 10)], Hit(late, soon)));
        await Assert.That(a.BlockedText).IsEqualTo("Limit hit · resets in 5h 00m");
        await Assert.That(b.BlockedText).IsEqualTo("Limit hit · resets in 5h 00m");
    }

    [Test]
    public async Task Unknown_reset_constraint_reads_limit_hit_alone() {
        var view = Project(Reading([W("claude", 300, 10)], Hit(Constraint("claude", 300, Now.AddHours(1)), Constraint(null, null, null))));
        await Assert.That(view.BlockedText).IsEqualTo("Limit hit");
    }

    [Test]
    public async Task Next_reset_is_the_earliest_primary_reset() {
        var account = Account(Reading([
            W("claude", 300, 10, Now.AddHours(3)),
            W("claude", 10080, 10, Now.AddHours(1)),
            W("sonnet", 300, 10, Now.AddMinutes(5)),
            W("claude", 60, 10, Now.AddMinutes(-5))
        ]));
        await Assert.That(AccountUsage.NextReset(account, Now)).IsEqualTo(Now.AddHours(1));
        await Assert.That(AccountUsage.Project(account, Now, Utc).NextResetText).IsEqualTo("Week resets in 1h 00m");
    }

    [Test]
    public async Task Age_from_latest_confirmed() {
        var view = Project(Reading([W("claude", 300, 10, confirmed: Now.AddMinutes(-30)), W("claude", 10080, 10, confirmed: Now.AddMinutes(-3))]));
        await Assert.That(view.AgeText).IsEqualTo("updated 3m ago");
    }

    [Test]
    public async Task Unconfirmed_windows_read_freshness_unknown() {
        var window = new UsageWindowDto("claude", 300, 10, Now.AddHours(1), null);
        await Assert.That(Project(Reading([window])).AgeText).IsEqualTo("freshness unknown");
    }

    [Test]
    public async Task Future_confirmed_reads_updated_just_now() =>
        await Assert.That(Project(Reading([W("claude", 300, 10, confirmed: Now.AddMinutes(5))])).AgeText).IsEqualTo("updated just now");

    [Test]
    public async Task Ready_positive_case() =>
        await Assert.That(Project(Reading([W("claude", 300, 40), W("claude", 10080, 89.4)])).Ready).IsTrue();

    [Test]
    public async Task Not_ready_when_one_window_is_stale_beside_a_fresh_one() =>
        await Assert.That(Project(Reading([W("claude", 300, 40), W("claude", 10080, 40, confirmed: Now.AddMinutes(-61))])).Ready).IsFalse();

    [Test]
    public async Task Not_ready_at_90_rounded() =>
        await Assert.That(Project(Reading([W("claude", 300, 89.6)])).Ready).IsFalse();

    [Test]
    public async Task Not_ready_when_expired() =>
        await Assert.That(Project(Reading([W("claude", 300, 10, Now.AddMinutes(-1))])).Ready).IsFalse();

    [Test]
    public async Task Not_ready_when_not_recording_or_signed_out() {
        var reading = Reading([W("claude", 300, 10)]);
        await Assert.That(AccountUsage.Project(Account(reading, state: "installed"), Now, Utc).Ready).IsFalse();
        await Assert.That(AccountUsage.Project(Account(reading, signIn: SignInStates.SignedOut), Now, Utc).Ready).IsFalse();
        await Assert.That(AccountUsage.Project(Account(reading, signIn: null), Now, Utc).Ready).IsFalse();
    }

    [Test]
    public async Task Main_constraint_kept_live_by_an_exhausted_per_model_window_still_blocks() {
        var t0 = Now.AddMinutes(-20);
        var t1 = Now.AddMinutes(-10);
        var first = new ReadingObservation(
            t0, true,
            [new("claude", 300, 95, t0.AddHours(2), null), new("claude", 10080, 40, t0.AddDays(2), null), new("sonnet", 10080, 100, t0.AddDays(3), null)],
            null, null, [new LimitSignal("claude", 300)], true, true);
        var second = new ReadingObservation(
            t1, true,
            [new("claude", 300, 10, t1.AddHours(5), null), new("claude", 10080, 40, t0.AddDays(2), null), new("sonnet", 10080, 100, t0.AddDays(3), null)],
            null, null, [], false, true);

        var merged = ReadingMerge.Apply(ReadingMerge.Apply(ReadingMerge.Empty(1), first), second);
        var account = Account(merged);

        await Assert.That(AccountUsage.IsBlocked(account, Now)).IsTrue();
        await Assert.That(AccountUsage.Project(account, Now, Utc).BlockedText).IsNotNull();
    }

    [Test]
    public async Task Needs_fix_reasons() {
        var reading = Reading([W("claude", 300, 10)]);
        await Assert.That(AccountUsage.NeedsFix(Account(reading, signIn: SignInStates.SignedOut))).IsEqualTo("Needs sign-in");
        await Assert.That(AccountUsage.NeedsFix(Account(reading, "codex", "installed"))).IsEqualTo("Needs approval in Codex");
        await Assert.That(AccountUsage.NeedsFix(Account(reading, state: "broken"))).IsEqualTo("Needs repair");
        await Assert.That(AccountUsage.NeedsFix(Account(reading, state: "not_wired"))).IsEqualTo("Not set up");
        await Assert.That(AccountUsage.NeedsFix(Account(reading))).IsNull();
    }

    [Test]
    public async Task Equal_views_compare_equal() {
        var a = Project(Reading([W("claude", 300, 40), W("claude", 10080, 10)]));
        var b = Project(Reading([W("claude", 300, 40), W("claude", 10080, 10)]));
        var c = Project(Reading([W("claude", 300, 41), W("claude", 10080, 10)]));
        await Assert.That(a).IsEqualTo(b);
        await Assert.That(a.GetHashCode()).IsEqualTo(b.GetHashCode());
        await Assert.That(a.Equals(c)).IsFalse();
    }
}
