using System.Reflection;
using Avalonia.Controls.ApplicationLifetimes;

namespace Capacitor.App.Tests.Unit;

// Avalonia prohibits implementing this interface in C#; DispatchProxy supplies the test double.
public class FakeActivatableLifetime : DispatchProxy {
    EventHandler<ActivatedEventArgs>? _activated;

    public IActivatableLifetime Lifetime => (IActivatableLifetime)this;

    public static FakeActivatableLifetime Create() =>
        (FakeActivatableLifetime)Create<IActivatableLifetime, FakeActivatableLifetime>();

    public void Raise(ActivationKind kind) => _activated?.Invoke(this, new ActivatedEventArgs(kind));

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) {
        switch (targetMethod?.Name) {
            case "add_Activated": _activated += (EventHandler<ActivatedEventArgs>)args![0]!; return null;
            case "remove_Activated": _activated -= (EventHandler<ActivatedEventArgs>)args![0]!; return null;
            case "add_Deactivated":
            case "remove_Deactivated": return null;
            case "TryEnterBackground":
            case "TryLeaveBackground": return false;
            default: throw new NotSupportedException($"Unexpected activation call: {targetMethod?.Name}");
        }
    }
}
