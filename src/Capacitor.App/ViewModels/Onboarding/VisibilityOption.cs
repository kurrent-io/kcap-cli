namespace Capacitor.App.ViewModels.Onboarding;

/// One answer to "who can read new sessions". A dedicated record rather than a ValueTuple —
/// Avalonia's reflection-based bindings need real CLR properties.
public sealed record VisibilityOption(string Value, string Label, string Detail);
