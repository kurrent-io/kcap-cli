using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Setup;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels.Onboarding;

/// Setup can do nothing for a machine with no harness, so the wizard asks before its first step.
/// Only a completed detection that found nothing stops setup: a failed one passes, and the
/// Harnesses step reports it with its own retry.
public sealed class HarnessPreflight : ReactiveObject {
    public const string Title = "Install a harness first";

    public static string Message { get; } =
        $"kcap records the sessions of coding-agent harnesses such as Claude Code and Codex, and found none on " +
        $"this {(OperatingSystem.IsMacOS() ? "Mac" : "computer")}. Install a harness, then {SetupLocation.Relaunch}";

    readonly Func<CancellationToken, Task<IReadOnlyDictionary<HarnessId, DetectedAgent>>> _detect;
    HarnessPreflightState _state;

    public HarnessPreflight(Func<CancellationToken, Task<IReadOnlyDictionary<HarnessId, DetectedAgent>>> detect) =>
        _detect = detect;

    public HarnessPreflightState State {
        get => _state;
        private set => this.RaiseAndSetIfChanged(ref _state, value);
    }

    public async Task RunAsync(CancellationToken ct) {
        try {
            var found = await _detect(ct).ConfigureAwait(true);
            State = found.Values.Any(d => d.Detected) ? HarnessPreflightState.Passed : HarnessPreflightState.NoneFound;
        } catch (OperationCanceledException) {
            // Shutdown: there is no setup left to gate.
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: harness pre-flight failed, continuing setup: {ex.Message}");
            State = HarnessPreflightState.Passed;
        }
    }
}
