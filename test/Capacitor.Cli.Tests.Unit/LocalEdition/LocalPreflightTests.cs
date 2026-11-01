using TUnit.Core.Enums;
using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using System.Text;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.LocalEdition;

namespace Capacitor.Cli.Tests.Unit.LocalEdition;

public class LocalPreflightTests {
    const string Version = "1.2.3";

    static readonly LocalPlatform MacArm = new(true, false, false, Architecture.Arm64);

    [TempDir] public required TempDir Tmp { get; init; }

    LocalRoot Root => field ??= new(Tmp.PathTo("local"));

    ConfigRoot Config => field ??= new(Tmp.PathTo("cfg"));

    sealed class FakeService(UnitOwner? owner = null) : ILocalServerService {
        public string Describe => "fake";
        public void Install(LocalServerUnit unit) => throw new NotSupportedException();
        public void Start(LocalServerUnit unit) => throw new NotSupportedException();
        public Task StopAsync(LocalServerUnit unit, CancellationToken ct) => throw new NotSupportedException();
        public Task UninstallAsync(LocalServerUnit unit, CancellationToken ct) => throw new NotSupportedException();
        public string UnitFile(LocalServerUnit unit) => throw new NotSupportedException();
        public bool IsInstalled(LocalServerUnit unit) => throw new NotSupportedException();
        public UnitOwner Owner(LocalServerUnit unit) => owner ?? new(OwnerReading.Absent.Instance, OwnerReading.Absent.Instance);
        public UnitActivity Activity(LocalServerUnit unit) => throw new NotSupportedException();
    }

    // A temp directory on macOS is already ~85 bytes, so tests off the socket subject lift the limit; the
    // socket tests use the platform's own limit on a root under /tmp.
    LocalPreflight Preflight(LocalRoot? root = null, LocalPlatform? platform = null, ILocalServerService? service = null, bool realLimit = false,
        params ClientCopy[] copies) =>
        new(root ?? Root, Config, platform ?? MacArm, service ?? new FakeService(), realLimit ? null : int.MaxValue, new FakeCopies(copies));

    sealed class FakeCopies(ClientCopy[] copies) : ILocalClientCopies {
        public IReadOnlyList<ClientCopy> Find() => copies;
    }

    static async Task<LocalInstallFailure> Refusal(Func<LocalPlan> run) {
        try {
            run();
        } catch (LocalInstallFailure failure) {
            return failure;
        }

        throw new InvalidOperationException("The preflight passed.");
    }

    static void ActiveRegistry(LocalRoot root, int port = 51234) {
        Directory.CreateDirectory(root.Directory);
        LocalStateFiles.PublishRegistry(root, new(new("id1", port, $"http://localhost:{port}", Version), []));
    }

    static void Journal(LocalRoot root) {
        Directory.CreateDirectory(root.Directory);
        LocalStateFiles.WriteJournal(root, new("setup", "pending", ServerStart.Deny, null, DateTimeOffset.UtcNow, new Dictionary<string, string>()));
    }

    string WriteConfig(string json) => Tmp.CreateFile("cfg/config.json", json);

    [Test]
    [Arguments(true, false, false, Architecture.Arm64, null)]
    [Arguments(true, false, false, Architecture.X64, LocalInstallFailureCode.UnsupportedPlatform)]
    [Arguments(false, true, true, Architecture.X64, LocalInstallFailureCode.MuslUnsupported)]
    [Arguments(false, true, true, Architecture.Arm64, LocalInstallFailureCode.MuslUnsupported)]
    [Arguments(false, true, false, Architecture.X64, LocalInstallFailureCode.UnsupportedPlatform)]
    [Arguments(false, false, false, Architecture.X64, LocalInstallFailureCode.UnsupportedPlatform)]
    public async Task The_platform_matrix(bool mac, bool linux, bool musl, Architecture arch, LocalInstallFailureCode? expected) {
        var preflight = Preflight(platform: new(mac, linux, musl, arch));

        if (expected is null) {
            await Assert.That(preflight.Check(Version).Hint).IsEqualTo(PlanHint.Install);
            return;
        }

        var failure = await Refusal(() => preflight.Check(Version));
        await Assert.That(failure.Code).IsEqualTo(expected.Value);
    }

    [Test]
    public async Task An_intel_mac_is_told_so_and_a_musl_linux_is_told_musl() {
        var intel = await Refusal(() => Preflight(platform: new(true, false, false, Architecture.X64)).Check(Version));
        var musl  = await Refusal(() => Preflight(platform: new(false, true, true, Architecture.X64)).Check(Version));

        await Assert.That(intel.Message).Contains("Intel");
        await Assert.That(musl.Message).Contains("musl builds of kcap do not support a local installation");
    }

    [Test]
    public async Task The_socket_limit_is_103_on_macos_and_107_on_linux() {
        await Assert.That(LocalPreflight.SocketLimit(MacArm)).IsEqualTo(103);
        await Assert.That(LocalPreflight.SocketLimit(new(false, true, false, Architecture.X64))).IsEqualTo(107);
    }

    // The longer of the two sockets is the embedded KurrentDB's `/data/kurrentdb/kurrent.sock`, 28 bytes after the root.
    const int LongestSuffix = 28;

    static LocalRoot RootWithBytes(int rootBytes, string fill = "a") {
        var baseDir = "/tmp";
        var probe   = LocalPreflight.SocketPaths(new(Path.Combine(baseDir, "a"))).Min(p => p.Bytes) - "/run/.s.PGSQL.5432".Length;
        var room    = rootBytes - (probe - 1);
        var name    = new StringBuilder();
        var width   = Encoding.UTF8.GetByteCount(fill);

        for (var used = 0; used + width <= room; used += width) name.Append(fill);
        while (Encoding.UTF8.GetByteCount(name.ToString()) < room) name.Append('a');

        return new(Path.Combine(baseDir, name.ToString()));
    }

    [Test]
    public async Task The_sockets_are_the_ones_the_server_binds() {
        var root  = new LocalRoot(Tmp.PathTo("local"));
        var paths = LocalPreflight.SocketPaths(root).Select(p => p.Path).ToList();

        await Assert.That(LocalSockets.KurrentDb(root)).IsEqualTo(Path.Combine(root.Directory, "data", "kurrentdb", "kurrent.sock"));
        await Assert.That(LocalSockets.Postgres(root)).IsEqualTo(Path.Combine(root.Directory, "run", ".s.PGSQL.5432"));
        await Assert.That(paths.Count).IsEqualTo(2);
        await Assert.That(paths.Any(p => p.EndsWith(Path.Combine("data", "kurrentdb", "kurrent.sock"), StringComparison.Ordinal))).IsTrue();
        await Assert.That(paths.Any(p => p.EndsWith(Path.Combine("run", ".s.PGSQL.5432"), StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Socket_path_one_byte_under_the_limit_passes() {
        var root = RootWithBytes(103 - LongestSuffix - 1);

        await Assert.That(LocalPreflight.SocketPaths(root).Max(p => p.Bytes)).IsEqualTo(102);
        await Assert.That(Preflight(root, realLimit: true).Check(Version).Hint).IsEqualTo(PlanHint.Install);
    }

    [Test]
    public async Task Socket_path_at_the_limit_passes() {
        var root = RootWithBytes(103 - LongestSuffix);

        await Assert.That(LocalPreflight.SocketPaths(root).Max(p => p.Bytes)).IsEqualTo(103);
        await Assert.That(Preflight(root, realLimit: true).Check(Version).Hint).IsEqualTo(PlanHint.Install);
    }

    [Test]
    public async Task Socket_path_one_byte_over_is_refused_naming_only_the_socket_that_does_not_fit() {
        var root    = RootWithBytes(103 - LongestSuffix + 1);
        var failure = await Refusal(() => Preflight(root, realLimit: true).Check(Version));

        await Assert.That(failure.Code).IsEqualTo(LocalInstallFailureCode.SocketPathTooLong);
        await Assert.That(failure.Message).Contains("kurrent.sock is 104 bytes");
        await Assert.That(failure.Message).DoesNotContain(".s.PGSQL.5432");
        await Assert.That(failure.Message).Contains($"Set {LocalRoot.EnvVar} to a directory at least 1 byte shorter.");
    }

    /// <summary>An 80-byte root fits the Postgres socket, so a check of that socket alone passes a root whose
    /// KurrentDB socket the server cannot bind.</summary>
    [Test]
    public async Task An_80_byte_root_is_refused_for_its_kurrentdb_socket() {
        var root    = RootWithBytes(80);
        var failure = await Refusal(() => Preflight(root, realLimit: true).Check(Version));

        await Assert.That(LocalPreflight.SocketPaths(root).Min(p => p.Bytes)).IsLessThanOrEqualTo(103);
        await Assert.That(failure.Code).IsEqualTo(LocalInstallFailureCode.SocketPathTooLong);
        await Assert.That(failure.Message).Contains("kurrent.sock is 108 bytes");
        await Assert.That(failure.Message).Contains("at least 5 bytes shorter");
    }

    [Test]
    public async Task Both_sockets_over_the_limit_are_both_named() {
        var root    = RootWithBytes(103);
        var failure = await Refusal(() => Preflight(root, realLimit: true).Check(Version));

        await Assert.That(failure.Message).Contains(".s.PGSQL.5432 is 121 bytes");
        await Assert.That(failure.Message).Contains("kurrent.sock is 131 bytes");
        await Assert.That(failure.Message).Contains("at least 28 bytes shorter");
    }

    [Test]
    [ExcludeOn(OS.Windows), UnsupportedOSPlatform("windows")]
    public async Task A_root_spelled_through_a_link_is_measured_as_the_server_binds_it() {
        var target = Tmp.CreateDir("t");
        var link   = Tmp.PathTo(new string('l', 60));
        Directory.CreateSymbolicLink(link, target.Path);
        var root   = new LocalRoot(Path.Combine(link, "local"));

        var kurrent = LocalPreflight.SocketPaths(root).Single(p => p.Path.EndsWith("kurrent.sock", StringComparison.Ordinal));

        await Assert.That(kurrent.Path).IsEqualTo(LocalSockets.KurrentDb(root));
        await Assert.That(kurrent.Bytes).IsEqualTo(Encoding.UTF8.GetByteCount(LocalSockets.KurrentDb(root)));
    }

    [Test]
    public async Task Socket_length_counts_utf8_bytes() {
        var root  = RootWithBytes(103 - LongestSuffix, fill: "ø");
        var paths = LocalPreflight.SocketPaths(root);

        await Assert.That(root.Directory).Contains("ø");
        await Assert.That(paths.Max(p => p.Bytes)).IsEqualTo(103);
        await Assert.That(paths.Max(p => p.Path.Length)).IsLessThan(103);

        var over = RootWithBytes(103 - LongestSuffix + 2, fill: "ø");
        var failure = await Refusal(() => Preflight(over, realLimit: true).Check(Version));

        await Assert.That(failure.Code).IsEqualTo(LocalInstallFailureCode.SocketPathTooLong);
    }

    [Test]
    [ExcludeOn(OS.Windows), UnsupportedOSPlatform("windows")]
    public async Task A_root_under_a_symlink_cycle_is_refused() {
        var a = Tmp.PathTo("a");
        var b = Tmp.PathTo("b");
        Directory.CreateSymbolicLink(a, b);
        Directory.CreateSymbolicLink(b, a);

        var failure = await Refusal(() => Preflight(new(Path.Combine(a, "local"))).Check(Version));

        await Assert.That(failure.Code).IsEqualTo(LocalInstallFailureCode.UnsafeRoot);
    }

    [Test]
    [ExcludeOn(OS.Windows), UnsupportedOSPlatform("windows")]
    public async Task A_world_writable_ancestor_is_refused() {
        File.SetUnixFileMode(Tmp.Path, (UnixFileMode)0b111_111_111);

        var failure = await Refusal(() => Preflight().Check(Version));

        await Assert.That(failure.Code).IsEqualTo(LocalInstallFailureCode.UnsafeRoot);
    }

    [Test]
    [ExcludeOn(OS.Windows), UnsupportedOSPlatform("windows")]
    public async Task A_sticky_world_writable_ancestor_passes() {
        File.SetUnixFileMode(Tmp.Path, (UnixFileMode)0b111_111_111 | UnixFileMode.StickyBit);

        await Assert.That(Preflight().Check(Version).Hint).IsEqualTo(PlanHint.Install);
    }

    [Test]
    public async Task A_profile_for_another_server_is_refused() {
        WriteConfig("""{"version":2,"active_profile":"work","profiles":{"work":{"server_url":"https://cloud.example.com"}}}""");

        var failure = await Refusal(() => Preflight().Check(Version));

        await Assert.That(failure.Code).IsEqualTo(LocalInstallFailureCode.OtherServer);
        await Assert.That(failure.Message).Contains("work");
        await Assert.That(failure.Message).Contains("https://cloud.example.com");
    }

    [Test]
    public async Task A_registry_with_an_active_installation_is_reuse() {
        ActiveRegistry(Root);
        WriteConfig("""{"version":2,"active_profile":"default","profiles":{"default":{"server_url":"http://127.0.0.1:51234"}}}""");
        Directory.CreateDirectory(Path.Combine(Root.Directory, "data"));

        var plan = Preflight().Check(Version);

        await Assert.That(plan.Hint).IsEqualTo(PlanHint.Reuse);
        await Assert.That(plan.Existing!.Port).IsEqualTo(51234);
    }

    [Test]
    public async Task An_unreadable_registry_is_refused() {
        Tmp.CreateFile("local/install.json", "{ not json");

        var failure = await Refusal(() => Preflight().Check(Version));

        await Assert.That(failure.Code).IsEqualTo(LocalInstallFailureCode.RegistryUnreadable);
    }

    [Test]
    public async Task A_setup_journal_is_recover_even_with_data_and_a_corrupt_config() {
        Journal(Root);
        Directory.CreateDirectory(Path.Combine(Root.Directory, "data"));
        WriteConfig("{ not json");

        var plan = Preflight().Check(Version);

        await Assert.That(plan.Hint).IsEqualTo(PlanHint.Recover);
        await Assert.That(plan.Existing).IsNull();
    }

    [Test]
    public async Task Recheck_after_recovery_refuses_a_journal_still_present() {
        Journal(Root);

        await Assert.That(() => Preflight().Recheck(Version)).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Recheck_runs_the_slot_and_config_checks_a_recover_check_skipped() {
        Directory.CreateDirectory(Path.Combine(Root.Directory, "data"));

        var failure = await Refusal(() => Preflight().Recheck(Version));

        await Assert.That(failure.Code).IsEqualTo(LocalInstallFailureCode.LeftoverState);
    }

    [Test]
    [Arguments("bin")]
    [Arguments("data")]
    [Arguments("run")]
    [Arguments("plist")]
    [Arguments("label")]
    public async Task Leftover_bin_data_run_plist_or_label_each_refuse_and_survive(string leftover) {
        var other = new OwnerReading.Root("/Users/other/.config/kcap/local");
        var owner = leftover switch {
            "plist" => new UnitOwner(OwnerReading.Absent.Instance, other),
            "label" => new UnitOwner(other, OwnerReading.Absent.Instance),
            _       => null,
        };

        string? survivor = null;
        if (owner is null) survivor = Path.Combine(Root.Directory, leftover);
        if (survivor is not null) Directory.CreateDirectory(survivor);

        var failure = await Refusal(() => Preflight(service: new FakeService(owner)).Check(Version));

        await Assert.That(failure.Code).IsEqualTo(LocalInstallFailureCode.LeftoverState);
        await Assert.That(failure.Message).Contains(leftover is "plist" or "label" ? other.Path : leftover);
        if (survivor is not null) await Assert.That(Directory.Exists(survivor)).IsTrue();
    }

    [Test]
    public async Task An_unreadable_config_is_refused() {
        WriteConfig("{ not json");

        var failure = await Refusal(() => Preflight().Check(Version));

        await Assert.That(failure.Code).IsEqualTo(LocalInstallFailureCode.ConfigUnreadable);
    }

    [Test]
    [Arguments("/abs")]
    [Arguments("../x")]
    [Arguments("not-semver")]
    [Arguments("1.2.3\n")]
    [Arguments("1.2.3 ")]
    public async Task An_invalid_version_override_is_refused(string version) {
        var failure = await Refusal(() => Preflight().Check(version));

        await Assert.That(failure.Code).IsEqualTo(LocalInstallFailureCode.InvalidArguments);
    }

    /// <summary>The edition choice offers local on the same predicate the preflight refuses by.</summary>
    [Test]
    [Arguments(true, false, false, Architecture.Arm64, true)]
    [Arguments(true, false, false, Architecture.X64, false)]
    [Arguments(false, true, false, Architecture.Arm64, false)]
    public async Task Only_an_Apple_silicon_Mac_or_an_x64_PC_supports_the_local_edition(bool mac, bool linux, bool musl, Architecture arch, bool expected) =>
        await Assert.That(new LocalPlatform(mac, linux, musl, arch).SupportsLocalEdition).IsEqualTo(expected);

    [Test]
    public async Task An_x64_Windows_PC_installs_the_win_bundle_and_an_Arm_one_is_told_it_needs_x64() {
        var x64 = new LocalPlatform(false, false, false, Architecture.X64) { IsWindows = true };
        var arm = new LocalPlatform(false, false, false, Architecture.Arm64) { IsWindows = true };

        await Assert.That(x64.Rid).IsEqualTo("win-x64");
        await Assert.That(Preflight(platform: x64).Check(Version).Hint).IsEqualTo(PlanHint.Install);

        var refused = await Refusal(() => Preflight(platform: arm).Check(Version));
        await Assert.That(refused.Code).IsEqualTo(LocalInstallFailureCode.UnsupportedPlatform);
        await Assert.That(refused.Message).Contains("x64");
    }

    [Test]
    public async Task Copies_newer_than_the_floor_pass() {
        var plan = Preflight(copies: [
            new(ClientCopyKind.OnPath, "/usr/local/bin/kcap", "1.3.1-alpha.0.1"),
            new(ClientCopyKind.DesktopApp, "/Applications/kcap.app/Contents/MacOS/kcap", "9.0.0"),
        ]).Check(Version);

        await Assert.That(plan.Hint).IsEqualTo(PlanHint.Install);
    }

    [Test]
    public async Task A_kcap_on_path_below_the_floor_is_refused_naming_it() {
        var failure = await Refusal(() => Preflight(copies: new ClientCopy(ClientCopyKind.OnPath, "/usr/local/bin/kcap", "1.2.0")).Check(Version));

        await Assert.That(failure.Code).IsEqualTo(LocalInstallFailureCode.ClientBelowFloor);
        await Assert.That(failure.Message).IsEqualTo(
            "The kcap on PATH (/usr/local/bin/kcap) is 1.2.0, and a local installation needs a kcap newer than 1.3.0. Update or remove it before setting up a local installation.");
    }

    [Test]
    public async Task The_desktop_apps_kcap_below_the_floor_is_refused_naming_the_app() {
        var failure = await Refusal(() => Preflight(copies:
            new ClientCopy(ClientCopyKind.DesktopApp, "/Applications/kcap.app/Contents/MacOS/kcap", "1.3.0")).Check(Version));

        await Assert.That(failure.Code).IsEqualTo(LocalInstallFailureCode.ClientBelowFloor);
        await Assert.That(failure.Message).IsEqualTo(
            "The desktop app's kcap (/Applications/kcap.app/Contents/MacOS/kcap) is 1.3.0, and a local installation needs a kcap newer than 1.3.0. Update the desktop app before setting up a local installation.");
    }

    [Test]
    public async Task A_copy_that_reports_no_version_is_refused() {
        var failure = await Refusal(() => Preflight(copies: new ClientCopy(ClientCopyKind.OnPath, "/usr/local/bin/kcap", null)).Check(Version));

        await Assert.That(failure.Code).IsEqualTo(LocalInstallFailureCode.ClientBelowFloor);
        await Assert.That(failure.Message).IsEqualTo(
            "The kcap on PATH (/usr/local/bin/kcap) did not report its version, and a local installation needs a kcap newer than 1.3.0. Update or remove it before setting up a local installation.");
    }

    [Test]
    public async Task A_reuse_is_refused_too() {
        ActiveRegistry(Root);
        WriteConfig("""{"version":2,"active_profile":"default","profiles":{"default":{"server_url":"http://127.0.0.1:51234"}}}""");

        var failure = await Refusal(() => Preflight(copies: new ClientCopy(ClientCopyKind.OnPath, "/usr/local/bin/kcap", "1.2.0")).Check(Version));

        await Assert.That(failure.Code).IsEqualTo(LocalInstallFailureCode.ClientBelowFloor);
    }

    /// <summary>An interrupted setup is finished or undone whatever else is installed; the old copy is refused after.</summary>
    [Test]
    public async Task An_old_copy_waits_until_recovery_has_run() {
        var old = new ClientCopy(ClientCopyKind.OnPath, "/usr/local/bin/kcap", "1.2.0");
        Journal(Root);

        await Assert.That(Preflight(copies: old).Check(Version).Hint).IsEqualTo(PlanHint.Recover);

        File.Delete(Root.JournalPath);
        var failure = await Refusal(() => Preflight(copies: old).Recheck(Version));

        await Assert.That(failure.Code).IsEqualTo(LocalInstallFailureCode.ClientBelowFloor);
    }

    [Test]
    public async Task A_refused_platform_asks_no_copy() {
        var copies = new CountingCopies();

        await Refusal(() => new LocalPreflight(Root, Config, new(true, false, false, Architecture.X64), new FakeService(), int.MaxValue, copies).Check(Version));

        await Assert.That(copies.Calls).IsEqualTo(0);
    }

    sealed class CountingCopies : ILocalClientCopies {
        public int Calls { get; private set; }

        public IReadOnlyList<ClientCopy> Find() {
            Calls++;
            return [];
        }
    }
}
