using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Commands;

public sealed class ExperimentalCommand {
    static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    static readonly TimeSpan ConfirmWindow = TimeSpan.FromSeconds(5);
    static readonly TimeSpan ProbeBudget = TimeSpan.FromSeconds(2);

    readonly ConfigRoot _config;
    readonly TimeProvider _time;
    readonly Func<string, CancellationToken, Task<ProbeResult>> _probe;
    readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public ExperimentalCommand(ConfigRoot config, DaemonStore daemons, TimeProvider time)
        : this(config, time,
            (name, ct) => LocalControlProbe.ProbeAsync(daemons, name, time, ProbeBudget, ct),
            (span, ct) => Task.Delay(span, time, ct)) { }

    internal ExperimentalCommand(
            ConfigRoot config, TimeProvider time,
            Func<string, CancellationToken, Task<ProbeResult>> probe,
            Func<TimeSpan, CancellationToken, Task> delay) {
        _config = config;
        _time   = time;
        _probe  = probe;
        _delay  = delay;
    }

    public async Task<int> HandleAsync(string[] args) {
        var sub = args.Length > 1 ? args[1] : "list";

        return sub switch {
            "list"                          => await ListAsync(),
            "enable" or "disable"           => await SetAsync(args, sub == "enable"),
            _                               => await UsageAsync()
        };
    }

    static async Task<int> UsageAsync() {
        await Console.Error.WriteLineAsync("Usage: kcap experimental [list | enable <feature> | disable <feature>]");

        return 1;
    }

    async Task<(string Profile, string DaemonName)> ResolveAsync() {
        var profiles = await AppConfig.LoadProfileConfig(_config);
        var name     = profiles.Active?.Daemon?.Name;

        return (profiles.ActiveName, DaemonNameResolver.Resolve([], name));
    }

    async Task<int> ListAsync() {
        var (profile, daemonName) = await ResolveAsync();
        var store = new ExperimentalSettingsStore(_config);
        var saved = store.Load(profile);
        if (!saved.Readable) return await RefuseCorruptAsync(store, saved);

        var probe = await _probe(daemonName, CancellationToken.None);

        foreach (var feature in ExperimentalFeatures.All) {
            var on = saved.IsEnabled(feature.Id);
            var (kind, reason) = Classify(on, probe, feature.Id);
            await Console.Out.WriteLineAsync($"{feature.Id}  {(on ? "on" : "off")}  {StateText(kind, reason)}");
            await Console.Out.WriteLineAsync($"  {feature.Description}");
        }

        return 0;
    }

    async Task<int> SetAsync(string[] args, bool enable) {
        if (args.Length < 3) return await UsageAsync();

        var feature = ExperimentalFeatures.Find(args[2]);
        if (feature is null) {
            await Console.Error.WriteLineAsync(
                $"Unknown experimental feature '{args[2]}'. Known: {string.Join(", ", ExperimentalFeatures.All.Select(f => f.Id))}");

            return 1;
        }

        var (profile, daemonName) = await ResolveAsync();
        var store = new ExperimentalSettingsStore(_config);

        if (!await store.SetAsync(profile, feature.Id, enable, CancellationToken.None))
            return await RefuseCorruptAsync(store, store.Load(profile));

        await Console.Out.WriteLineAsync($"{(enable ? "Enabled" : "Disabled")} {feature.Id} (profile: {profile})");
        await Console.Out.WriteLineAsync(await ConfirmAsync(enable, daemonName, feature.Id));

        return 0;
    }

    async Task<string> ConfirmAsync(bool saved, string daemonName, string featureId) {
        var deadline = _time.GetUtcNow() + ConfirmWindow;

        while (true) {
            var probe = await _probe(daemonName, CancellationToken.None);
            var (kind, reason) = Classify(saved, probe, featureId);

            switch (kind) {
                case ExperimentalFeatureStateKind.Active or ExperimentalFeatureStateKind.Off:
                    return "Applied live";
                case ExperimentalFeatureStateKind.OnNotActive:
                    return $"Saved, but not active: {reason}";
                case ExperimentalFeatureStateKind.AppliedLocally:
                    return "Applied locally; the server has not been told yet";
                case ExperimentalFeatureStateKind.Unknown:
                    return "Saved; the running daemon is too old to apply it. Update kcap and restart the daemon";
                case ExperimentalFeatureStateKind.DaemonNotRunning:
                    return "Saved; the daemon is not running and will apply it when it starts";
            }

            if (_time.GetUtcNow() >= deadline)
                return "Saved; the daemon did not confirm in time. Run kcap experimental to check";

            await _delay(PollInterval, CancellationToken.None);
        }
    }

    // A reachable daemon whose snapshot did not arrive is still settling, not too old.
    static (ExperimentalFeatureStateKind Kind, string? Reason) Classify(bool saved, ProbeResult probe, string featureId) =>
        probe.Reachable && probe.Snapshot is null
            ? (ExperimentalFeatureStateKind.Applying, null)
            : ExperimentalFeatureState.Classify(saved, probe.Reachable, probe.Snapshot, featureId);

    static string StateText(ExperimentalFeatureStateKind kind, string? reason) => kind switch {
        ExperimentalFeatureStateKind.Off              => "applied",
        ExperimentalFeatureStateKind.Active           => "active",
        ExperimentalFeatureStateKind.OnNotActive      => $"not active: {reason}",
        ExperimentalFeatureStateKind.Applying         => "applying",
        ExperimentalFeatureStateKind.AppliedLocally   => "applied locally; the server has not been told yet",
        ExperimentalFeatureStateKind.Unknown          => "the running daemon is too old to report it",
        ExperimentalFeatureStateKind.DaemonNotRunning => "daemon not running; applies when it starts",
        _                                             => kind.ToString()
    };

    static async Task<int> RefuseCorruptAsync(ExperimentalSettingsStore store, ExperimentalSettingsRead read) {
        await Console.Error.WriteLineAsync($"{read.Problem} ({store.Path}). Fix or remove the file, then try again");

        return 1;
    }
}
