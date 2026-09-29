namespace Capacitor.Cli.Capture;

internal sealed class RedactionBudget(TimeProvider time, TimeSpan limit) {
    // A scan of a whole recording has no watcher loop to keep responsive.
    public static readonly RedactionBudget Unlimited = new(TimeProvider.System, TimeSpan.MaxValue);

    public RedactionBudget(TimeProvider time) : this(time, TimeSpan.FromSeconds(1)) { }

    readonly long _start = time.GetTimestamp();

    public void Check() {
        if (time.GetElapsedTime(_start) >= limit) throw new RedactionBudgetExceededException();
    }
}
