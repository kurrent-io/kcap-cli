namespace Capacitor.Cli.Tests.Unit.LocalEdition.Fakes;

/// <summary>Records each report on the reporting thread, so the list is complete when the call returns.</summary>
sealed class RecordingProgress<T> : IProgress<T> {
    public List<T> Reports { get; } = [];

    public void Report(T value) => Reports.Add(value);
}
