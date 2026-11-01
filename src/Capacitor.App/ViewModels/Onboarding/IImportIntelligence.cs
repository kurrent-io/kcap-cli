using Capacitor.Cli.Core.Setup.Intelligence;

namespace Capacitor.App.ViewModels.Onboarding;

/// <summary>The background intelligence answer as the Import page uses it.</summary>
public interface IImportIntelligence {
    /// <summary>What the server applied; null when nothing was.</summary>
    IntelligenceSetupResponse? Applied { get; }

    /// <summary>Sends the same answer again with this choice; null when no answer was sent.</summary>
    Task<IntelligenceSetupOutcome?> ApplyImportOutcomesAsync(bool importOutcomes, CancellationToken ct);
}
