using Capacitor.App.Services;
using Capacitor.Cli.Core.Setup;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels.Onboarding;

public sealed class PathFixViewModel : ReactiveObject {
    readonly ICliPathInstaller _installer;
    readonly IAppStateStore    _store;
    readonly string?           _target;
    readonly Func<bool>        _onLocalPath;

    Task<bool>? _running;
    bool    _offerClaimed;
    bool    _fixed;
    string? _message;
    IReadOnlyList<DisclosureSpan> _plan = [];

    public PathFixViewModel(ICliPathInstaller installer, IAppStateStore store, string? target, Func<bool>? onLocalPath = null) {
        _installer   = installer;
        _store       = store;
        _target      = target;
        _onLocalPath = onLocalPath ?? (() => false);
        if (target is not null) _ = DescribeAsync(target);
    }

    // An inconclusive PATH probe never offers an install.
    public static bool ComputeApplicable(bool hasInstaller, string? target, bool? kcapOnPath) =>
        hasInstaller && target is not null && kcapOnPath == false;

    /// What installing will do on this machine; empty until the installer has looked.
    public IReadOnlyList<DisclosureSpan> Plan {
        get => _plan;
        private set => this.RaiseAndSetIfChanged(ref _plan, value);
    }

    async Task DescribeAsync(string target) {
        try {
            Plan = await _installer.DescribeAsync(target, CancellationToken.None).ConfigureAwait(true);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            Plan = [new(_installer.Disclosure)];
        }
    }

    public bool Fixed {
        get => _fixed;
        private set => this.RaiseAndSetIfChanged(ref _fixed, value);
    }

    /// Why the last install did not finish, or null.
    public string? Message {
        get => _message;
        private set => this.RaiseAndSetIfChanged(ref _message, value);
    }

    /// Settles with why the last link did not finish, or null when it did or none ran. The app's
    /// startup offer waits on it, since setup may never have shown it.
    public async Task<string?> UnfinishedAsync() {
        if (_running is null) return null;
        await _running.ConfigureAwait(true);
        return Fixed ? null : Message;
    }

    /// Installs the terminal command and reports whether the login shell can now run it. A call
    /// while one is running joins it, so two links never race for the same folder.
    public async Task<bool> InstallAsync(CancellationToken ct = default) {
        if (_target is not null) await ClaimOfferedOnceAsync().ConfigureAwait(true);
        return await LinkAsync(ct).ConfigureAwait(true);
    }

    /// Like InstallAsync, but claims no offer: a failure nobody was shown is left for the app's
    /// startup offer to raise.
    public Task<bool> LinkAsync(CancellationToken ct) =>
        _running is { IsCompleted: false } running ? running : _running = RunInstallAsync(ct);

    async Task<bool> RunInstallAsync(CancellationToken ct) {
        if (Fixed) return true;
        if (_target is null) {
            Message = "This machine could not find its own kcap, so nothing changed.";
            return false;
        }

        Message = null;
        try {
            Apply(await _installer.InstallAsync(_target, ct).ConfigureAwait(true));
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            return false;
        } catch (Exception ex) {
            Message = $"Could not install the terminal command: {ex.Message}. Try again.";
        }
        return Fixed;
    }

    // Claim before installing so a retry cannot trigger another startup offer.
    Task ClaimOfferedOnceAsync() {
        if (_offerClaimed) return Task.CompletedTask;
        _offerClaimed = true;
        return _store.UpdateAsync(s => s.ShimOffered ? s : s with { ShimOffered = true });
    }

    void Apply(ShimResult result) {
        switch (result.Outcome) {
            case ShimOutcome.Installed:
                Fixed   = true;
                Message = null;
                break;
            case ShimOutcome.InstalledButNotOnPath:
                Message = _onLocalPath()
                    ? "Installed, but your terminal cannot find it yet, so harnesses started from a terminal may not record."
                    : result.Detail ?? "Installed, but your terminal cannot find kcap yet. Open a new terminal and run kcap --version.";
                break;
            default: // Failed
                Message = $"Could not finish installing the terminal command. {result.Detail}".TrimEnd();
                break;
        }
    }
}
