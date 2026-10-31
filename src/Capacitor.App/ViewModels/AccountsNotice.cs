namespace Capacitor.App.ViewModels;

/// A failed or unconfirmed account change. A success has none: the list itself shows the change.
public sealed record AccountsNotice(string Title, string? Detail, bool IsWarning = false);
