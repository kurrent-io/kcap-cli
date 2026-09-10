namespace Capacitor.App.Services;

public sealed record AcpAnswer(IReadOnlyList<string> SelectedOptionIds, string? FreeText);
