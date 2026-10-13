namespace Capacitor.Cli.Capture;

public enum RedactionLossReason { InputLimit, MalformedInput, RegexTimeout, RecordBudget, OutputLimit }

public static class RedactionLossReasonExtensions {
    // Both limits are wall-clock, so contention, a GC pause or sleep can trip them on a line a
    // later attempt redacts; the others are properties of the line itself.
    public static bool IsTransient(this RedactionLossReason reason) =>
        reason is RedactionLossReason.RegexTimeout or RedactionLossReason.RecordBudget;
}
