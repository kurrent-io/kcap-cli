namespace Capacitor.Cli.Capture;

internal sealed class RedactionBudget(TimeProvider time) {
    readonly long _start = time.GetTimestamp();

    public void Check() {
        if (time.GetElapsedTime(_start) >= TimeSpan.FromSeconds(1))
            throw new RedactionBudgetExceededException();
    }
}
