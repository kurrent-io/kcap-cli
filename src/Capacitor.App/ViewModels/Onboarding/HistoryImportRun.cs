using System.Text.RegularExpressions;
using Capacitor.App.Services;
using Capacitor.Cli.Core;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels.Onboarding;

/// <summary>
/// The History page's import, which outlives the page: one <c>kcap import</c> pass per level
/// (<c>--private</c> is per invocation), counted live from its per-session lines and settled by each
/// pass's own closing line. Cancelled only by the app shutting down.
/// </summary>
public sealed partial class HistoryImportRun : ReactiveObject {
    internal const int LogLimit = 500;

    readonly CancellationTokenSource _cts = new();
    readonly Action<Action>          _post;

    ImportRunState _state = ImportRunState.Running;
    int            _imported;
    int            _failed;
    int            _passesDone;
    int            _processed;
    int            _passImported;
    int            _passFailed;

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

    /// Sessions this run has settled so far, whatever their outcome.
    public int Processed {
        get => _processed;
        private set => this.RaiseAndSetIfChanged(ref _processed, value);
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

        var text = line.Text.Trim();
        if (Totals().Match(text) is { Success: true } m &&
            int.TryParse(m.Groups[1].Value, out var imported) && int.TryParse(m.Groups[3].Value, out var failed)) {
            // The closing line is authoritative: it also counts what never printed a session line.
            Imported      = Add(Imported, imported - _passImported);
            Failed        = Add(Failed, failed - _passFailed);
            _passImported = 0;
            _passFailed   = 0;
            return;
        }

        if (SessionLine().Match(text) is not { Success: true } session) return;

        Processed = Add(Processed, 1);
        switch (session.Groups[1].Value) {
            case "Loading":
                _passImported++;
                Imported = Add(Imported, 1);
                break;
            case "Failed":
                _passFailed++;
                Failed = Add(Failed, 1);
                break;
        }
    });

    static int Add(int total, int delta) => (int)Math.Clamp((long)total + delta, 0, int.MaxValue);

    // Each pass closes with this line.
    [GeneratedRegex(@"^(\d+) imported · (\d+) skipped · (\d+) failed$")]
    private static partial Regex Totals();

    // One line per session as it settles, printed by the redirected-output import loop.
    [GeneratedRegex(@"^(Loading|Skipping|Failed|Already loaded|Refreshed) [^\s()]+(?: \(.*\))?$")]
    private static partial Regex SessionLine();
}
