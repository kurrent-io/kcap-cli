namespace Capacitor.Cli.LocalEdition;

/// <summary>Reports on the caller's thread, in order. <see cref="Progress{T}"/> posts to a context instead, so
/// events could arrive after the call that raised them returned.</summary>
sealed class SyncProgress<T>(Action<T> report) : IProgress<T> {
    public void Report(T value) => report(value);
}
