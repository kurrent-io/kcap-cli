using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Cli.LocalEdition;

/// <param name="Existing">The registry's active installation when the hint is <see cref="PlanHint.Reuse"/>, else null.</param>
sealed record LocalPlan(PlanHint Hint, string Version, LocalInstallation? Existing);
