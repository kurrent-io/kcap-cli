namespace Capacitor.Cli.Core;

/// The words `launchctl print` shows on its `spawn type = &lt;word&gt; (&lt;n&gt;)` line. A Standard job
/// and a plist with no ProcessType both print `daemon`; `adaptive` and `background` are the
/// throttled band. Any other word proves nothing, so it is never read as either.
public static class SpawnTypes {
    public const string Daemon      = "daemon";
    public const string Interactive = "interactive";
    public const string Adaptive    = "adaptive";
    public const string Background  = "background";

    public static SpawnTypeReading Classify(string? word) => word switch {
        Daemon or Interactive  => SpawnTypeReading.Positive,
        Adaptive or Background => SpawnTypeReading.BackgroundBand,
        _                      => SpawnTypeReading.Unknown,
    };

    public static bool IsPositive(string? word)       => Classify(word) == SpawnTypeReading.Positive;
    public static bool IsBackgroundBand(string? word) => Classify(word) == SpawnTypeReading.BackgroundBand;
}
