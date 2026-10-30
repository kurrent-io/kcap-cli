using Capacitor.App.Services;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Accounts.Readings;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Harness.Codex;

namespace Capacitor.App.ViewModels;

/// The one projection of an account's reading that every usage surface renders. Pure: the
/// clock and zone are arguments.
public static class AccountUsage {
    public const int WarningPercent    = 75;
    public const int ReadyBelowPercent = 90;

    public static readonly TimeSpan ReadyFreshness = TimeSpan.FromHours(1);

    public const string NoReading = "no reading yet";
    const string Unknown   = "usage unknown";

    public static AccountUsageView Project(AccountViewDto account, DateTimeOffset now, TimeZoneInfo zone) {
        var reading = account.Reading;
        if (reading is null) return Line(NoReading);

        var main    = MainLimitId(account.Vendor);
        var blocked = LiveMainConstraints(account, now);
        var pill    = BlockedText(blocked, now, zone);

        if (reading.Windows.Length == 0)
            return blocked.Count == 0 ? Line(NoReading) : Line(Unknown) with { BlockedText = pill };

        var primary = reading.Windows.Where(w => w.LimitId == main).OrderBy(w => w.WindowMinutes).ToArray();
        var others  = reading.Windows.Where(w => w.LimitId != main).Select(w => w.LimitId).Distinct().Count();
        var hits    = ModelHits(reading, main, now);
        var perModel = others == 0 ? null : hits == 0 ? $"+{others} per model" : $"+{others} per model · {hits} hit";
        var age     = AgeText(reading, now);

        if (primary.Length == 0)
            return new([], $"{Unknown} · {perModel}", null, pill, null, age, false);

        var bars = primary.Select(w => Bar(w, blocked, now, zone)).ToArray();

        return new(bars, null, perModel, pill, NextResetText(account, now, zone), age, IsReady(account, primary, blocked, now));
    }

    public static bool IsBlocked(AccountViewDto account, DateTimeOffset now) => LiveMainConstraints(account, now).Count > 0;

    /// Why the account cannot record or sign in, or null when it needs nothing.
    public static string? NeedsFix(AccountViewDto account) =>
        account.SignIn == SignInStates.SignedOut                                   ? "Needs sign-in"
        : RecordingStateLabels.IsAttention(account.Vendor, account.RecordingState) ? RecordingStateLabels.Label(account.Vendor, account.RecordingState)
        : null;

    /// The earliest future reset among the main limit's windows.
    public static DateTimeOffset? NextReset(AccountViewDto account, DateTimeOffset now) => NextResetWindow(account, now)?.ResetsAt;

    /// The main-limit window that resets first, or null when none has a future reset.
    public static UsageWindowDto? NextResetWindow(AccountViewDto account, DateTimeOffset now) =>
        account.Reading is { } reading
            ? reading.Windows.Where(w => w.LimitId == MainLimitId(account.Vendor) && w.ResetsAt > now).MinBy(w => w.ResetsAt)
            : null;

    static AccountUsageView Line(string text) => new([], text, null, null, null, null, false);

    /// Null for a vendor whose readings are not modelled: none of its windows is account-wide.
    static string? MainLimitId(string vendor) => vendor.ToLowerInvariant() switch {
        "claude" => ClaudeStatusLineReading.LimitId,
        "codex"  => CodexRateLimits.DefaultLimitId,
        _        => null
    };

    static IReadOnlyList<BlockConstraintDto> LiveMainConstraints(AccountViewDto account, DateTimeOffset now) {
        if (account.Reading is not { } reading) return [];

        var main = MainLimitId(account.Vendor);
        return ReadingMerge.LiveConstraints(reading, now).Where(c => c.LimitId is null || c.LimitId == main).ToArray();
    }

    static int ModelHits(AccountReadingDto reading, string? main, DateTimeOffset now) =>
        ReadingMerge.LiveConstraints(reading, now).Where(c => c.LimitId is not null && c.LimitId != main).Select(c => c.LimitId).Distinct().Count();

    static string? BlockedText(IReadOnlyList<BlockConstraintDto> blocked, DateTimeOffset now, TimeZoneInfo zone) =>
        blocked.Count == 0                      ? null
        : blocked.Any(c => c.ResetsAt is null)  ? "Limit hit"
        : "Limit hit · " + UsageText.Reset(blocked.Max(c => c.ResetsAt)!.Value, now, zone);

    static UsageBar Bar(UsageWindowDto window, IReadOnlyList<BlockConstraintDto> blocked, DateTimeOffset now, TimeZoneInfo zone) {
        var label = UsageText.WindowLabel(window.WindowMinutes);

        if (Expired(window, now)) return new(label, null, "reset", UsageTone.Unknown, "new window · usage not read yet");

        var percent = Rounded(window);
        var tone    = percent >= 100 || blocked.Any(c => c.WindowMinutes == window.WindowMinutes) ? UsageTone.Danger
                    : percent >= WarningPercent ? UsageTone.Warning
                    : UsageTone.Neutral;

        return new(label, percent, $"{percent}%", tone, window.ResetsAt is { } at ? UsageText.Reset(at, now, zone) : null);
    }

    static bool IsReady(AccountViewDto account, UsageWindowDto[] primary, IReadOnlyList<BlockConstraintDto> blocked, DateTimeOffset now) =>
        account.RecordingState == RecordingStates.Token(RecordingState.Recording)
     && account.SignIn == SignInStates.SignedIn
     && blocked.Count == 0
     && primary.All(w => !Expired(w, now)
                      && w.ConfirmedAt is { } confirmed && now - confirmed <= ReadyFreshness
                      && Rounded(w) < ReadyBelowPercent);

    static string? NextResetText(AccountViewDto account, DateTimeOffset now, TimeZoneInfo zone) =>
        NextResetWindow(account, now) is { ResetsAt: { } at } window
            ? $"{UsageText.WindowLabel(window.WindowMinutes)} {UsageText.Reset(at, now, zone)}"
            : null;

    static string AgeText(AccountReadingDto reading, DateTimeOffset now) =>
        reading.Windows.Max(w => w.ConfirmedAt) is { } at ? "updated " + RelativeTime.Format(at, now) : "freshness unknown";

    static bool Expired(UsageWindowDto window, DateTimeOffset now) => window.ResetsAt is { } at && at <= now;

    static int Rounded(UsageWindowDto window) => Math.Clamp((int)Math.Round(window.UsedPercent, MidpointRounding.AwayFromZero), 0, 100);
}
