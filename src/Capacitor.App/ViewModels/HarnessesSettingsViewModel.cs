using System.Reactive;
using System.Reactive.Linq;
using Capacitor.App.Services;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Setup;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

public sealed class HarnessesSettingsViewModel : ReactiveObject, IDisposable {
    readonly SettingsProfileStore _store;
    readonly IKcapCli _cli;
    readonly Func<HarnessId, bool>? _isWired;
    readonly Func<CancellationToken, Task<IReadOnlyDictionary<HarnessId, DetectedAgent>>> _detect;
    readonly CancellationTokenSource _lifetime;
    IReadOnlyList<HarnessSettingsRow> _rows = [];
    bool _busy;
    string? _message;

    public HarnessesSettingsViewModel(SettingsProfileStore store, IKcapCli cli,
            Func<CancellationToken, Task<IReadOnlyDictionary<HarnessId, DetectedAgent>>> detect,
            CancellationToken appLifetime = default, Func<HarnessId, bool>? isWired = null) {
        _store = store;
        _cli = cli;
        _isWired = isWired;
        _detect = detect;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(appLifetime);
        var idle = this.WhenAnyValue(x => x.Busy).Select(b => !b);
        RescanCommand = ReactiveCommand.CreateFromTask(RescanAsync, idle);
        ConnectCommand = ReactiveCommand.CreateFromTask<HarnessSettingsRow>(ConnectAsync, idle);
    }

    public IReadOnlyList<HarnessSettingsRow> Rows { get => _rows; private set => this.RaiseAndSetIfChanged(ref _rows, value); }
    public bool Busy { get => _busy; private set => this.RaiseAndSetIfChanged(ref _busy, value); }
    public string? Message { get => _message; private set => this.RaiseAndSetIfChanged(ref _message, value); }
    public string ProfileName => _store.ProfileName;
    public ReactiveCommand<Unit, Unit> RescanCommand { get; }
    public ReactiveCommand<HarnessSettingsRow, Unit> ConnectCommand { get; }

    public async Task RescanAsync() {
        if (Busy) return;
        Busy = true;
        Message = "Looking for harnesses…";
        try {
            Rows = await Task.Run(async () => {
                var detected = await _detect(_lifetime.Token).ConfigureAwait(false);
                return HarnessRegistry.Identities.Where(h => detected.TryGetValue(h.Id, out var d) && d.Detected)
                    .Select(h => new HarnessSettingsRow(h, detected[h.Id], _isWired?.Invoke(h.Id))).ToArray();
            }, _lifetime.Token);
            Message = Rows.Count == 0 ? "No harnesses found. Install a harness, then rescan." : null;
        } catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) {
        } catch (Exception ex) {
            Message = $"Could not detect harnesses: {ex.Message}";
        } finally { Busy = false; }
    }

    internal async Task ConnectAsync(HarnessSettingsRow row) {
        if (Busy || !Rows.Contains(row)) return;
        Busy = true;
        row.Message = "Configuring…";
        try {
            await Task.Run(_store.LoadProfile, _lifetime.Token);
            var result = await _cli.PluginInstallAsync(row.Id.PluginInstallFlag, _lifetime.Token);
            bool? wired = _isWired is null ? null : await Task.Run(() => _isWired(row.Id), _lifetime.Token);
            row.SetWired(wired);
            row.Message = result is { ExitCode: 0, TimedOut: false }
                ? "Setup completed. Restart this harness to load the changes."
                : result.TimedOut ? "Setup timed out. Try connecting again."
                : string.IsNullOrWhiteSpace(result.Stderr) ? $"Setup failed (exit {result.ExitCode})." : result.Stderr.Trim();
        } catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) {
        } catch (Exception ex) {
            row.Message = $"Could not configure this harness: {ex.Message}";
        } finally { Busy = false; }
    }

    public void Dispose() {
        _lifetime.Cancel();
        RescanCommand.Dispose();
        ConnectCommand.Dispose();
        _lifetime.Dispose();
    }
}
