using System.Text.RegularExpressions;
using Capacitor.App.Services;
using Capacitor.Cli.Core;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels.Onboarding;

/// <summary>
/// The History page's import, which outlives the page: one <c>kcap import</c> pass per level
/// (<c>--private</c> is per invocation), counted from each pass's own closing line. Cancelled only
/// by the app shutting down.
/// </summary>
public sealed partial class HistoryImportRun : ReactiveObject {
    internal const int LogLimit = 500;

    readonly CancellationTokenSource _cts = new();
    readonly Action<Action>          _post;

    ImportRunState _state = ImportRunState.Running;
    int            _imported;
    int            _failed;
    int            _passesDone;

    internal HistoryImportRun(int expected, int repositories, string windowCaption, int passes, Action<Action> post) {
        Expected      = expected;
        Repositories  = repositories;
        WindowCaption = windowCaption;
        Passes        = passes;
        _post         = post;
    }

    /// Sessions the selection should bring, from discovery; 0 when it could not say.
    public int    Expected      { get; }
    public int    Repositories  { get; }
    public int    Passes        { get; }
    public string WindowCaption { get; }

    public ImportRunState State {
        get => _state;
        private set {
            this.RaiseAndSetIfChanged(ref _state, value);
            this.RaisePropertyChanged(nameof(Running));
        }
    }

    public bool Running => State == ImportRunState.Running;

    public int Imported {
        get => _imported;
        private set => this.RaiseAndSetIfChanged(ref _imported, value);
    }

    public int Failed {
        get => _failed;
        private set => this.RaiseAndSetIfChanged(ref _failed, value);
    }

    public int PassesDone {
        get => _passesDone;
        private set => this.RaiseAndSetIfChanged(ref _passesDone, value);
    }

    public List<string> Log { get; } = [];

    internal Task Completion { get; private set; } = Task.CompletedTask;

    internal void Start(IKcapCli cli, IReadOnlyList<ImportRequest> passes) => Completion = RunAsync(cli, passes);

    public async Task CancelAsync() {
        await _cts.CancelAsync().ConfigureAwait(false);
        try { await Completion.ConfigureAwait(false); }
        catch (Exception ex) { Console.Error.WriteLine($"kcap: history import failed unexpectedly: {ex.Message}"); }
    }

    async Task RunAsync(IKcapCli cli, IReadOnlyList<ImportRequest> passes) {
        var anyFailed = false;
        try {
            foreach (var pass in passes) {
                _cts.Token.ThrowIfCancellationRequested();
                var result = await cli.ImportAsync(pass, OnLine, _cts.Token).ConfigureAwait(false);
                anyFailed |= result.ExitCode != 0 || result.TimedOut;
                _post(() => PassesDone++);
            }

            _cts.Token.ThrowIfCancellationRequested();
            _post(() => State = anyFailed || Failed > 0 ? ImportRunState.Failed : ImportRunState.Finished);
        } catch (OperationCanceledException) {
            _post(() => State = ImportRunState.Cancelled);
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: history import failed: {ex.Message}");
            _post(() => State = ImportRunState.Failed);
        }
    }

    void OnLine(StreamedLine line) => _post(() => {
        Log.Add(line.Text);
        if (Log.Count > LogLimit) Log.RemoveAt(0);

        if (Totals().Match(line.Text.Trim()) is { Success: true } m &&
            int.TryParse(m.Groups[1].Value, out var imported) && int.TryParse(m.Groups[3].Value, out var failed)) {
            Imported = (int)Math.Min(int.MaxValue, (long)Imported + imported);
            Failed = (int)Math.Min(int.MaxValue, (long)Failed + failed);
        }
    });

    // Each pass closes with this line; it is the only count the run prints that is not a diagnostic.
    [GeneratedRegex(@"^(\d+) imported · (\d+) skipped · (\d+) failed$")]
    private static partial Regex Totals();
}
