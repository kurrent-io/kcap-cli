using System.ComponentModel;
using System.Reactive;
using System.Reactive.Linq;
using Capacitor.App.Services;
using Capacitor.Cli.Core;
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
    readonly IAppStateStore? _toolsMemory;
    readonly CancellationTokenSource _lifetime;
    IReadOnlyList<HarnessSettingsRow> _rows = [];
    bool _busy;
    bool _scanning;
    string? _message;

    public HarnessesSettingsViewModel(SettingsProfileStore store, IKcapCli cli,
            Func<CancellationToken, Task<IReadOnlyDictionary<HarnessId, DetectedAgent>>> detect,
            CancellationToken appLifetime = default, Func<HarnessId, bool>? isWired = null,
            Func<CancellationToken, Task<string?>>? ensureTerminalCommand = null, IAppStateStore? toolsMemory = null) {
        _store = store;
        _cli = cli;
        _isWired = isWired;
        _detect = detect;
        _ensureTerminalCommand = ensureTerminalCommand;
        _toolsMemory = toolsMemory;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(appLifetime);
        var idle = this.WhenAnyValue(x => x.Busy, x => x.Scanning, (busy, scanning) => !busy && !scanning);
        RescanCommand = ReactiveCommand.CreateFromTask(RescanAsync, idle);
        ConnectCommand = ReactiveCommand.CreateFromTask<HarnessSettingsRow>(ConnectAsync, idle);
        ToggleConnectionCommand = ReactiveCommand.CreateFromTask<HarnessSettingsRow>(ToggleConnectionAsync, idle);
        ToggleToolsCommand = ReactiveCommand.CreateFromTask<HarnessSettingsRow>(ToggleToolsAsync, idle);
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

    public string RescanPrompt => Rows.Count == 0
        ? "No coding tools found. Install one, then rescan."
        : "Missing a tool you use? Install it, then rescan.";
    public ReactiveCommand<Unit, Unit> RescanCommand { get; }
    public ReactiveCommand<HarnessSettingsRow, Unit> ConnectCommand { get; }
    public ReactiveCommand<HarnessSettingsRow, Unit> ToggleConnectionCommand { get; }
    public ReactiveCommand<HarnessSettingsRow, Unit> ToggleToolsCommand { get; }

    public async Task RescanAsync() {
        if (Busy || Scanning) return;
        Scanning = true;
        Message = "Looking for coding tools…";
        try {
            var state = _toolsMemory is null ? null : await _toolsMemory.LoadAsync();
            var remembered = state?.HarnessToolsByVendor;
            Rows = await Task.Run(async () => {
                var detected = await _detect(_lifetime.Token).ConfigureAwait(false);
                return HarnessRegistry.Identities.Where(h => detected.TryGetValue(h.Id, out var d) && d.Detected)
                    .Select(h => {
                        var wired = _isWired?.Invoke(h.Id);
                        // With no answer on record, assume the default install: tools went in with the hooks.
                        var tools = remembered?.TryGetValue(h.Id.VendorId, out var t) == true ? t : wired == true;
                        return new HarnessSettingsRow(h, detected[h.Id], wired, tools,
                            setupUnverified: state?.UnverifiedHarnessSetups?.Contains(h.Id.VendorId) == true);
                    }).ToArray();
            }, _lifetime.Token);
            Message = null;
        } catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) {
        } catch (Exception ex) {
            Message = $"Could not look for coding tools: {ex.Message}";
        } finally { Scanning = false; }
    }

    internal const string PartlyInstalled = "Setup completed, but some parts were not installed. Check the details, then try again.";
    internal const string PartlyRemoved = "Disconnected, but some parts were left in place. Check the details.";
    internal const string PartlyKept = "Setup completed, but some parts were left in place. Check the details.";
    internal const string PartlyKeptAndInstalled =
        "Setup completed, but some parts were left in place and some were not installed. Check the details, then try again.";

    static string WarningsMessage(bool off, bool removalWarned, bool installWarned) =>
        off ? PartlyRemoved
        : removalWarned && installWarned ? PartlyKeptAndInstalled
        : removalWarned ? PartlyKept
        : PartlyInstalled;

    internal Task ToggleConnectionAsync(HarnessSettingsRow row) => ConfigureAsync(row, record: !row.IsConfigured, tools: row.Tools);

    internal Task ToggleToolsAsync(HarnessSettingsRow row) => ConfigureAsync(row, record: row.IsConfigured, tools: !row.Tools);

    internal Task ConnectAsync(HarnessSettingsRow row) => ConfigureAsync(row, record: true, tools: row.Tools, repair: true);

    async Task ConfigureAsync(HarnessSettingsRow row, bool record, bool tools, bool repair = false) {
        if (Busy || Scanning || !Rows.Contains(row)) return;
        if (!row.OffersTools) tools = record;
        var off = !record && !tools;
        Busy = true;
        row.Report(off ? "Disconnecting…" : "Updating…");
        var failed = off ? $"Could not disconnect {row.Label}"
            : record && !row.IsConfigured ? $"Could not connect {row.Label}"
            : $"Could not update {row.Label}";
        var mutationStarted = false;
        try {
            await Task.Run(_store.LoadProfile, _lifetime.Token);
            // Hooks call the bare kcap command, so recording first puts kcap on the terminal PATH, as setup does.
            if (record && _ensureTerminalCommand is { } ensure && await ensure(_lifetime.Token) is { } problem) {
                row.Report(problem, failed);
                return;
            }
            mutationStarted = true;
            await RememberSetupAsync(row, unverified: true);
            var flag = row.Id.PluginInstallFlag;
            ProcessResult result;
            // Removal warnings mean something stayed; install warnings mean something did not land.
            var removed = "";
            var installed = "";
            if (off) {
                result = await _cli.PluginRemoveAsync(flag, _lifetime.Token);
                removed = result.Stderr.Trim();
            } else {
                // Install only ever adds, so anything the new answer drops comes out first.
                var drops = !repair && ((row.IsConfigured && !record) || (row.OffersTools && row.Tools && !tools));
                result = drops ? await _cli.PluginRemoveAsync(flag, _lifetime.Token) : new ProcessResult(0, "", "", false);
                if (result is { ExitCode: 0, TimedOut: false }) {
                    removed = result.Stderr.Trim();
                    result = await _cli.PluginInstallAsync(flag, _lifetime.Token,
                        HarnessInstallOptions.For(row.Id, record, row.OffersTools ? tools : true));
                    installed = result.Stderr.Trim();
                    // A removal's warnings (configuration it preserved) still apply once the install succeeds.
                    if (removed.Length > 0) result = result with { Stderr = $"{removed}\n{installed}".TrimEnd() };
                }
            }
            bool? wired = _isWired is null ? null : await Task.Run(() => _isWired(row.Id), _lifetime.Token);
            row.SetWired(wired);
            if (result.TimedOut)
                row.Report("The change timed out. Rescan to check the connection before trying again.", failed);
            else if (result.ExitCode != 0)
                row.Report(string.IsNullOrWhiteSpace(result.Stderr)
                    ? $"kcap's command line stopped with an error (code {result.ExitCode}). Try again."
                    : $"kcap could not change some of {row.Label}'s files. The details name each one. Fix them, then try again.",
                    failed, result.Stderr);
            else if (wired is null)
                row.Report("The command finished, but the connection could not be verified. Rescan to check it.", "Could not verify the change");
            else if (wired.Value != record)
                row.Report(record ? "Recording is not configured yet. Try connecting again." : "Some recording setup remains. Try disconnecting again.", failed);
            else {
                row.Report(off
                    ? $"Disconnected. Restart {row.Label} to unload kcap. Existing sessions are kept."
                    : $"Setup completed. Restart {row.Label} to load the changes.");
                await RememberSetupAsync(row, unverified: false, tools: tools);
            }
            // The command line keeps restart reminders on stdout, so anything on stderr is a part that did not land.
            if (result is { ExitCode: 0, TimedOut: false } && !string.IsNullOrWhiteSpace(result.Stderr))
                row.Report(row.ErrorTitle is null ? WarningsMessage(off, removed.Length > 0, installed.Length > 0) : row.Message,
                    row.ErrorTitle ?? "Finished with warnings", result.Stderr, warning: row.ErrorTitle is null);
        } catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) {
            if (mutationStarted) row.SetWired(null);
        } catch (Exception ex) {
            if (mutationStarted) {
                try { row.SetWired(_isWired is null ? null : await Task.Run(() => _isWired(row.Id))); }
                catch { row.SetWired(null); }
            }
            // A missing or unlaunchable CLI surfaces as the OS's spawn error, which names a path but not the fix.
            row.Report(ex is Win32Exception
                ? "kcap could not start its command line tool. Reinstall kcap, then try again."
                : ex.Message, failed);
        } finally {
            row.SetWired(row.Wired);
            Busy = false;
        }
    }

    async Task RememberSetupAsync(HarnessSettingsRow row, bool unverified, bool? tools = null) {
        row.SetSetupUnverified(unverified);
        if (tools is { } installed) row.SetTools(installed);
        if (_toolsMemory is null) return;
        await _toolsMemory.UpdateAsync(state => state with {
            UnverifiedHarnessSetups = unverified
                ? [.. (state.UnverifiedHarnessSetups ?? []).Where(v => v != row.Id.VendorId), row.Id.VendorId]
                : [.. (state.UnverifiedHarnessSetups ?? []).Where(v => v != row.Id.VendorId)],
            HarnessToolsByVendor = tools is { } value && row.OffersTools
                ? new Dictionary<string, bool>(state.HarnessToolsByVendor ?? new Dictionary<string, bool>()) {
                    [row.Id.VendorId] = value,
                }
                : state.HarnessToolsByVendor,
        });
    }

    public void Dispose() {
        _lifetime.Cancel();
        RescanCommand.Dispose();
        ConnectCommand.Dispose();
        ToggleConnectionCommand.Dispose();
        ToggleToolsCommand.Dispose();
        _lifetime.Dispose();
    }
}
