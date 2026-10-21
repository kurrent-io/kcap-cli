using Capacitor.Cli.Commands;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>`kcap status` names the version each daemon runs, and warns about a daemon `kcap update`
/// does not reach: one on another version than the CLI, or one whose service runs another install.</summary>
internal sealed class StatusDaemonDriftTests {
    const string Cli    = "1.3.0-beta.9+822bc79";
    const string Ours   = "/home/u/.local/share/kcap/current/bin/kcap-daemon";
    const string NpmBin = "/opt/homebrew/lib/node_modules/@kurrent/kcap/bin/kcap-daemon";

    static StatusCommand.DaemonEntry Live(string version) => new("tony", 42, true, version);

    [Test]
    public async Task Daemon_line_shows_each_running_daemons_version() {
        var line = StatusCommand.DaemonLine([Live("1.3.0-beta.7+abc"), new("old", 7, true), new("gone", 8, false)]);

        await Assert.That(line).IsEqualTo("running (2) — tony (PID 42, 1.3.0-beta.7), old (PID 7)");
    }

    public static IEnumerable<Func<(string Case, IReadOnlyList<StatusCommand.DaemonEntry> Daemons, IReadOnlyList<(string, string?)> Units, bool VerifySupported, string? Expected)>> Cases() {
        yield return () => ("in step", [Live("1.3.0-beta.9+other")], [("tony", Ours)], true, null);
        yield return () => ("unit binary unreadable", [Live("1.3.0-beta.9")], [("tony", null)], true, null);
        yield return () => ("older daemon, service on another install", [Live("1.3.0-beta.7")], [("tony", NpmBin)], true,
            $"⚠ daemon 'tony' runs 1.3.0-beta.7, but this kcap is 1.3.0-beta.9; its service runs {NpmBin}, outside this kcap's install, so `kcap update` does not reach it. "
          + "Run `kcap daemon service install --replace --verify --name tony`.");
        yield return () => ("stopped service on another install", [], [("tony", NpmBin)], true,
            $"⚠ service 'tony' runs {NpmBin}, outside this kcap's install, so `kcap update` does not reach it. "
          + "Run `kcap daemon service install --replace --verify --name tony`.");
        yield return () => ("older daemon on systemd", [Live("1.3.0-beta.7")], [("tony", Ours)], false,
            "⚠ daemon 'tony' runs 1.3.0-beta.7, but this kcap is 1.3.0-beta.9. Run `kcap daemon service install --name tony`.");
        yield return () => ("older daemon without a service", [Live("1.3.0-beta.7")], [], true,
            "⚠ daemon 'tony' runs 1.3.0-beta.7, but this kcap is 1.3.0-beta.9. Run `kcap daemon stop --name tony`, then `kcap daemon start --name tony`.");
    }

    [Test]
    [MethodDataSource(nameof(Cases))]
    public async Task Drift_lines_name_the_fix(
            string @case, IReadOnlyList<StatusCommand.DaemonEntry> daemons, IReadOnlyList<(string, string?)> units,
            bool verifySupported, string? expected) {
        var lines = StatusCommand.DaemonDriftLines(Cli, daemons, units, Ours, verifySupported);

        string[] want = expected is null ? [] : [expected];
        await Assert.That(lines).IsEquivalentTo(want);
    }

    /// <summary>One unreadable unit keeps its place with no binary, so the units after it are still
    /// checked and the daemon is still advised as service-managed.</summary>
    [Test]
    public async Task An_unreadable_unit_is_kept_without_hiding_the_others() {
        var units = StatusCommand.ReadUnits(new UnitsManager(new() { ["a"] = null, ["b"] = NpmBin }));

        await Assert.That(units).IsEquivalentTo([("a", (string?)null), ("b", (string?)NpmBin)]);
    }

    sealed class UnitsManager(Dictionary<string, string?> binaries) : IServiceManager {
        public string Describe() => "units";
        public string UnitDirectory => "";
        public IReadOnlyList<GeneratedFile> GenerateFiles(ServiceSpec spec) => [];
        public IReadOnlyList<string> ListInstalled() => [.. binaries.Keys];
        public string? UnitBinary(string serviceId) =>
            binaries[serviceId] ?? throw new InvalidOperationException($"cannot read {serviceId}");
        public ServiceStatus Status(string serviceId) => throw new NotSupportedException();
        public IReadOnlyDictionary<string, string>? UnitEnvironment(string serviceId) => null;
        public ServiceQuery Query(string serviceId) => throw new NotSupportedException();
        public bool Install(ServiceSpec spec, bool startNow, out string? error) => throw new NotSupportedException();
        public void WriteAndBootstrap(ServiceSpec spec) => throw new NotSupportedException();
        public bool Uninstall(string serviceId, out string? error) => throw new NotSupportedException();
        public bool Start(string serviceId, out string? error) => throw new NotSupportedException();
        public bool Stop(string serviceId, out string? error) => throw new NotSupportedException();
    }
}
