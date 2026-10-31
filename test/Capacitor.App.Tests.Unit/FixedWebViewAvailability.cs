using Capacitor.App.Services;

namespace Capacitor.App.Tests.Unit;

sealed class FixedWebViewAvailability(bool available) : IWebViewAvailability {
    public bool IsAvailable { get; } = available;
}
