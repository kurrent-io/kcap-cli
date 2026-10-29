namespace Capacitor.Cli.Daemon.Services;

/// <summary>One <see cref="AgentActivityClock.TakeSubagentExpiries"/> result: whether a live id
/// was retired for age, and the time until the oldest survivor falls due (null when none is
/// live). Both come from the same instant, so an id is either retired by exactly one call or
/// counted in the deadline that call returns.</summary>
internal readonly record struct SubagentExpiry(bool RetiredAny, TimeSpan? NextDue);
