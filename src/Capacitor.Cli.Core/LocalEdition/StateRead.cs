namespace Capacitor.Cli.Core.LocalEdition;

/// <summary>What reading one of the lifecycle files found. Absent and unreadable are different
/// answers: an absent registry means no installation ever existed, while an unreadable one is not
/// evidence of anything and callers fail closed on it.</summary>
public abstract record StateRead<T> {
    StateRead() { }

    public sealed record Absent : StateRead<T>;

    public sealed record Present(T Value) : StateRead<T>;

    public sealed record Unreadable(string Path, string Reason) : StateRead<T>;
}
