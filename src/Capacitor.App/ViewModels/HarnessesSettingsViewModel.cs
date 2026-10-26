using System.ComponentModel;
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
    readonly Func<CancellationToken, Task<string?>>? _ensureTerminalCommand;
    readonly CancellationTokenSource _lifetime;
    IReadOnlyList<HarnessSettingsRow> _rows = [];
    bool _busy;
    bool _scanning;
    string? _message;

    public HarnessesSettingsViewModel(SettingsProfileStore store, IKcapCli cli,
            Func<CancellationToken, Task<IReadOnlyDictionary<HarnessId, DetectedAgent>>> detect,
            CancellationToken appLifetime = default, Func<HarnessId, bool>? isWired = null,
            Func<CancellationToken, Task<string?>>? ensureTerminalCommand = null) {
        _store = store;
        _cli = cli;
        _isWired = isWired;
        _detect = detect;
        _ensureTerminalCommand = ensureTerminalCommand;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(appLifetime);
        var idle = this.WhenAnyValue(x => x.Busy, x => x.Scanning, (busy, scanning) => !busy && !scanning);
        RescanCommand = ReactiveCommand.CreateFromTask(RescanAsync, idle);
        ConnectCommand = ReactiveCommand.CreateFromTask<HarnessSettingsRow>(ConnectAsync, idle);
        ToggleConnectionCommand = ReactiveCommand.CreateFromTask<HarnessSettingsRow>(ToggleConnectionAsync, idle);
    }

    public IReadOnlyList<HarnessSettingsRow> Rows {
        get => _rows;
        private set {
            this.RaiseAndSetIfChanged(ref _rows, value);
            this.RaisePropertyChanged(nameof(RescanPrompt));
        }
    }
    /// Connecting or disconnecting a tool: the harness operation closing Settings must wait for.
    public bool Busy { get => _busy; private set => this.RaiseAndSetIfChanged(ref _busy, value); }
    /// Looking for installed tools: read-only, so closing may abandon it.
    public bool Scanning { get => _scanning; private set => this.RaiseAndSetIfChanged(ref _scanning, value); }
    public string? Message { get => _message; private set => this.RaiseAndSetIfChanged(ref _message, value); }
    public string ProfileName => _store.ProfileName;

    public string RescanPrompt => Rows.Count == 0
        ? "No coding tools found. Install one, then rescan."
        : "Missing a tool you use? Install it, then rescan.";
    public ReactiveCommand<Unit, Unit> RescanCommand { get; }
    public ReactiveCommand<HarnessSettingsRow, Unit> ConnectCommand { get; }
    public ReactiveCommand<HarnessSettingsRow, Unit> ToggleConnectionCommand { get; }

    public async Task RescanAsync() {
        if (Busy || Scanning) return;
        Scanning = true;
        Message = "Looking for harnesses…";
        try {
            Rows = await Task.Run(async () => {
                var detected = await _detect(_lifetime.Token).ConfigureAwait(false);
                return HarnessRegistry.Identities.Where(h => detected.TryGetValue(h.Id, out var d) && d.Detected)
                    .Select(h => new HarnessSettingsRow(h, detected[h.Id], _isWired?.Invoke(h.Id))).ToArray();
            }, _lifetime.Token);
            Message = null;
        } catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) {
        } catch (Exception ex) {
            Message = $"Could not detect harnesses: {ex.Message}";
        } finally { Scanning = false; }
    }

    internal Task ToggleConnectionAsync(HarnessSettingsRow row) => ConfigureAsync(row, remove: row.IsConfigured);

    internal Task ConnectAsync(HarnessSettingsRow row) => ConfigureAsync(row, remove: false);

    async Task ConfigureAsync(HarnessSettingsRow row, bool remove) {
        if (Busy || Scanning || !Rows.Contains(row)) return;
        Busy = true;
        row.Report(remove ? "Disconnecting…" : "Connecting…");
        var failed = remove ? $"Could not disconnect {row.Label}" : $"Could not connect {row.Label}";
        try {
            await Task.Run(_store.LoadProfile, _lifetime.Token);
            // Hooks call the bare kcap command, so connecting first puts kcap on the terminal PATH, as setup does.
            if (!remove && _ensureTerminalCommand is { } ensure && await ensure(_lifetime.Token) is { } problem) {
                row.Report(problem, failed);
                return;
            }
            var result = remove
                ? await _cli.PluginRemoveAsync(row.Id.PluginInstallFlag, _lifetime.Token)
                : await _cli.PluginInstallAsync(row.Id.PluginInstallFlag, _lifetime.Token);
            bool? wired = _isWired is null ? null : await Task.Run(() => _isWired(row.Id), _lifetime.Token);
            row.SetWired(wired);
            if (result.TimedOut)
                row.Report("The change timed out. Rescan to check the connection before trying again.", failed);
            else if (result.ExitCode != 0)
                row.Report(string.IsNullOrWhiteSpace(result.Stderr)
                    ? $"The kcap command line exited with code {result.ExitCode}. Try again."
                    : result.Stderr.Trim(), failed);
            else if (wired is null)
                row.Report("The command finished, but the connection could not be verified. Rescan to check it.", "Could not verify the change");
            else if (wired.Value == remove)
                row.Report(remove ? "Some recording setup remains. Try disconnecting again." : "Recording is not configured yet. Try connecting again.", failed);
            else
                row.Report(remove
                    ? "Disconnected. Restart this harness to unload kcap. Existing sessions are kept."
                    : "Setup completed. Restart this harness to load the changes.");
            if (result is { ExitCode: 0, TimedOut: false } && !string.IsNullOrWhiteSpace(result.Stderr))
                row.Report($"{row.Message}\n{result.Stderr.Trim()}");
        } catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) {
        } catch (Exception ex) {
            // A missing or unlaunchable CLI surfaces as the OS's spawn error, which names a path but not the fix.
            row.Report(ex is Win32Exception
                ? "kcap could not start its command line tool. Reinstall kcap, then try again."
                : ex.Message, failed);
        } finally {
            row.SetWired(row.Wired);
            Busy = false;
        }
    }

    public void Dispose() {
        _lifetime.Cancel();
        RescanCommand.Dispose();
        ConnectCommand.Dispose();
        ToggleConnectionCommand.Dispose();
        _lifetime.Dispose();
    }
}
