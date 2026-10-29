using Capacitor.App.Services;

namespace Capacitor.App.ViewModels;

public sealed record AccountChoice(AccountRow Row, bool Selected, bool Removed);
