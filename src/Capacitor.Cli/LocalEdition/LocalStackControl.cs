using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Cli.LocalEdition;

/// <summary>
/// <c>kcap local start</c>, <c>stop</c> and <c>restart</c>. Each holds the lifecycle lease, so an interrupted setup is
/// finished or undone first, and decides who owns the per-user unit under the unit lock, where no other root can
/// replace it in between. A unit that is not provably this root's is never touched.
/// </summary>
sealed class LocalStackControl(LocalStack stack, Func<int, PortHolder?> findHolder) {
    static readonly TimeSpan PollGap = TimeSpan.FromSeconds(1);

    /// <summary>A server that exits this many times after a start will not come up without a change.</summary>
    const int RestartsBeforeGivingUp = 3;

    /// <summary>How long a just-started unit may still read as stopped before the start counts as having failed.</summary>
    static readonly TimeSpan StoppedGrace = TimeSpan.FromSeconds(15);

    /// <summary>Returns the line to end on. Failures throw <see cref="LocalInstallFailure"/> or
    /// <see cref="LocalLifecycleException"/>.</summary>
    public Task<string> StartAsync(IProgress<LocalInstallEvent> progress, Action<string> say, CancellationToken ct) =>
        stack.RunAsync(progress, async (_, state, token) => await StartAsync(Installed(state), say, token), ct);

    public Task<string> StopAsync(IProgress<LocalInstallEvent> progress, Action<string> say, CancellationToken ct) =>
        stack.RunAsync(progress, async (_, state, token) => await StopAsync(Installed(state), say, token), ct);

    public Task<string> RestartAsync(IProgress<LocalInstallEvent> progress, Action<string> say, CancellationToken ct) =>
        stack.RunAsync(progress, async (_, state, token) => {
            var installed = Installed(state);

            await StopAsync(installed, say, token);

            return await StartAsync(installed, say, token);
        }, ct);

    LocalStackState.Installed Installed(LocalStackState state) =>
        state switch {
            LocalStackState.Installed installed => installed,
            LocalStackState.NotInstalled => throw Failed($"No local installation in {stack.Root.Directory}. Run kcap setup --local to install one."),
            LocalStackState.Unreadable(var path, var reason) => throw Failed($"{path} cannot be read ({reason}). Nothing was changed."),
            _ => throw new InvalidOperationException($"Unexpected state {state} under the lifecycle lease."),
        };

    /// <summary>For a caller already holding the lifecycle lease. With <paramref name="rewriteUnit"/> an installed unit
    /// is written again, so a new version runs under the unit this kcap renders.</summary>
    public async Task<string> StartAsync(LocalStackState.Installed state, Action<string> say, CancellationToken ct, bool rewriteUnit = false) {
        var unit = state.Unit;

        if (!File.Exists(unit.ServerBinary))
            throw Failed($"The server's files are missing ({unit.ServerBinary}). Run kcap setup --local to restore them.");

        using (await LocalUnitLock.AcquireAsync(stack.Home, LocalUnitLock.Wait, stack.Time, ct)) {
            LocalOwnUnit.Ensure(stack.Root, unit, stack.Service, () => NotOurs(unit), startPresent: true, replace: rewriteUnit);
        }

        say($"Starting the local server on {unit.BaseUrl}…");

        await WaitReadyAsync(unit, ct);

        return await LocalServerProbe.AuthConfigAsync(stack.Local, unit.BaseUrl, ct) switch {
            AuthConfigAnswer.Lifted  => $"Ready on {unit.BaseUrl}",
            AuthConfigAnswer.Pending => throw Failed($"The server on {unit.BaseUrl} answers as an installation setup has not committed. Run kcap setup --local to finish it."),
            _ => throw Failed($"What answers on port {unit.Port} does not answer as this installation. kcap local status says more."),
        };
    }

    /// <summary>For a caller already holding the lifecycle lease.</summary>
    public async Task<string> StopAsync(LocalStackState.Installed state, Action<string> say, CancellationToken ct) {
        var unit = state.Unit;

        using (await LocalUnitLock.AcquireAsync(stack.Home, LocalUnitLock.Wait, stack.Time, ct)) {
            var owner = stack.Service.Owner(unit);

            if (owner is { Loaded: OwnerReading.Absent, OnDisk: OwnerReading.Absent }) {
                await LocalStackProbe.WaitUntilDownAsync(stack.Root, TimeSpan.Zero, stack.Time, ct);
                return "Not running: no unit is installed for it.";
            }

            if (LocalRootIdentity.Decide(stack.Root, owner) != OwnerDecision.Ours) throw NotOurs(unit, owner);

            say("Stopping the local server…");
            await stack.Service.StopAsync(unit, ct);
        }

        return "Stopped. It stays stopped, across logins too, until kcap local start.";
    }

    /// <summary>Waits as long as a migration may take, but gives up early on what no wait will fix: a stranger on the
    /// port, a server that exited for good, or one that keeps exiting.</summary>
    async Task WaitReadyAsync(LocalServerUnit unit, CancellationToken ct) {
        var started  = stack.Time.GetUtcNow();
        var deadline = started + LocalServerReadiness.StartBudget;
        var exits    = 0;
        int? lastPid = null, lastCode = null, firstStarts = null;

        while (true) {
            var health   = await LocalServerHealth.ReadAsync(stack.Local, unit, stack.Time, ct);
            var activity = stack.Service.Activity(unit);

            // With the process unknown, no listener can be called foreign.
            if (activity is not UnitActivity.Unknown && findHolder(unit.Port) is { } holder && holder.Pid != Pid(activity)) {
                // The process may have been replaced since the reading above.
                activity = stack.Service.Activity(unit);

                if (activity is not UnitActivity.Unknown && holder.Pid != Pid(activity))
                    throw Failed($"Port {unit.Port} is taken by {holder.Command} (pid {holder.Pid}), so the server cannot listen on it. "
                               + "The port is part of this installation's identity; stop that process and the server starts on its own, or run kcap local stop to stop it trying.");
            }

            if (health is ServerHealth.Answered { Ready: true } && activity is UnitActivity.Running) return;

            switch (activity) {
                case UnitActivity.Exited(var code):
                    throw Failed($"The server exited{ExitCode(code)} and will not be started again on its own. kcap local logs shows why.");

                // A clean exit on systemd, or a concurrent stop: nothing will start it again.
                case UnitActivity.Stopped when lastPid is not null || stack.Time.GetUtcNow() - started >= StoppedGrace:
                    throw Failed("The server stopped and will not be started again on its own. kcap local logs shows why; kcap local start starts it.");

                case UnitActivity.Running(var pid) when pid != lastPid:
                    if (lastPid is not null) exits++;
                    lastPid = pid;
                    break;

                case UnitActivity.Restarting(var code):
                    lastCode = code;
                    if (lastPid is not null) exits++;
                    lastPid = null;
                    break;
            }

            // The manager's own count sees exits between two polls.
            if (activity.Starts is { } starts) {
                firstStarts ??= starts;
                exits = Math.Max(exits, starts - firstStarts.Value);
            }

            if (exits >= RestartsBeforeGivingUp)
                throw Failed($"The server keeps exiting{ExitCode(lastCode)}. kcap local logs shows why; kcap local status shows what it is doing now.");

            if (stack.Time.GetUtcNow() >= deadline)
                throw Failed($"The local server did not report ready on {unit.BaseUrl} within {LocalServerReadiness.StartBudget.TotalMinutes:0} minutes. kcap local logs shows why.");

            await Task.Delay(PollGap, stack.Time, ct);
        }
    }

    LocalInstallFailure NotOurs(LocalServerUnit unit, UnitOwner? owner = null) {
        owner ??= stack.Service.Owner(unit);

        var roots = new[] { owner.Loaded, owner.OnDisk }
            .OfType<OwnerReading.Root>().Select(r => r.Path).Distinct(StringComparer.Ordinal).ToList();

        var serves = roots.Count > 0 ? $"; it names {string.Join(" and ", roots)}" : "";

        return Failed($"The {stack.Service.Describe} {unit.Label} ({stack.Service.UnitFile(unit)}) is not provably this installation's{serves}. Nothing was changed.");
    }

    static int? Pid(UnitActivity activity) => activity is UnitActivity.Running(var pid) ? pid : null;

    static string ExitCode(int? code) => code is { } c ? $" with code {c}" : "";

    static LocalInstallFailure Failed(string message) => new(LocalInstallFailureCode.StartFailed, message);
}
