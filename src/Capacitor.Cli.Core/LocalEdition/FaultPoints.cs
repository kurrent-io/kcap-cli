namespace Capacitor.Cli.Core.LocalEdition;

/// <summary>
/// Named places in the durable-write paths where a test can observe progress or throw to simulate a
/// crash. The hook is per async flow, so parallel tests never see each other's, and nothing armed
/// means <see cref="Hit"/> does nothing.
///
/// <para>Names: <c>rename:&lt;path&gt;</c> and <c>renamed:&lt;path&gt;</c> around a rename,
/// <c>sync:&lt;dir&gt;</c> before a directory fsync, <c>mkdir:&lt;path&gt;</c> after a mkdir,
/// <c>flush:&lt;path&gt;</c> before a file flush, <c>unlink:&lt;path&gt;</c> after a delete.</para>
/// </summary>
public static class FaultPoints {
    static readonly AsyncLocal<Action<string>?> Hook = new();

    public static IDisposable Arm(Action<string> onHit) {
        var previous = Hook.Value;
        Hook.Value = onHit;

        return new Disarm(previous);
    }

    public static void Hit(string point) => Hook.Value?.Invoke(point);

    sealed class Disarm(Action<string>? previous) : IDisposable {
        public void Dispose() => Hook.Value = previous;
    }
}
