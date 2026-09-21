namespace Capacitor.Cli.Core.Harness;

/// <summary>Whether a harness is present, for code that only needs the answer — never the
/// registry itself — so a test can supply one without a real <see cref="HarnessRegistry"/>.</summary>
public interface IHarnessDetection {
    bool Detected(HarnessId id);
}
