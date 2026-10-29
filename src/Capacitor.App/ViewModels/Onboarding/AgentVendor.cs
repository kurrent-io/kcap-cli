using Capacitor.Cli.Core.Harness;

namespace Capacitor.App.ViewModels.Onboarding;

/// One coding-agent vendor: its id, label and exclusive plugin-install flag (null = Claude's
/// flagless default). Order is display AND sequential-install order (Claude first).
internal sealed record AgentVendor(HarnessId Id, string Label, string? Flag);
