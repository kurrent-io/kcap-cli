namespace Capacitor.App.Services;

/// <param name="Since">The window's first day, or null for everything.</param>
public sealed record ImportDiscoveryWindow(DateOnly? Since, int Sessions);
