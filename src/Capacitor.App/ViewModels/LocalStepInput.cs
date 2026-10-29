using Capacitor.App.Services.LocalSetup;

namespace Capacitor.App.ViewModels;

/// The local server's part of an update: the run's progress, and the local stack banner's words for it.
public sealed record LocalStepInput(
    bool Running, LocalUpgradeOutcome? Outcome, string? Headline, string? Detail, bool Skipped = false,
    LocalUpgradePhase Phase = LocalUpgradePhase.Waiting, string? From = null, string? To = null, double? DownloadFraction = null);
