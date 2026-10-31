namespace Capacitor.App.Services.LocalSetup;

/// What the page offers after a failure, decided by the failure code alone.
public enum LocalInstallAffordance {
    Retry,
    Busy,
    Permanent,
}

public static class LocalInstallAffordanceExtensions {
    static readonly HashSet<string> RetryableCodes = new(StringComparer.Ordinal) {
        "download_failed", "start_failed", "verification_failed", "recovery_failed", "commit_failed", "cancelled", "client_below_floor",
    };

    extension(LocalInstallAffordance) {
        public static LocalInstallAffordance For(string? code) => code switch {
            null                                 => LocalInstallAffordance.Retry,
            "lifecycle_busy"                     => LocalInstallAffordance.Busy,
            _ when RetryableCodes.Contains(code) => LocalInstallAffordance.Retry,
            _                                    => LocalInstallAffordance.Permanent,
        };
    }
}
