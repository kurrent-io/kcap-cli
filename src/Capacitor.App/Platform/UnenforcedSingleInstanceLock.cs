namespace Capacitor.App.Platform;

public sealed class UnenforcedSingleInstanceLock : SingleInstanceLock {
    public override event Action? ShowRequested { add { } remove { } }
}
