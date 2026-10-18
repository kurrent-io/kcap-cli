using System.Diagnostics;
using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.LocalEdition;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Integration.LocalEdition;

/// <summary>
/// A SIGTERM delivered to the real <c>kcap setup --local --json</c> while the manifest download stalls. Opt-in with
/// <c>KCAP_LOCAL_SETUP_CHECK=1</c>, because the run consults this user's launchd for the production label. The
/// stall comes before any unit is written, so nothing is ever loaded; a machine where that label is already loaded
/// is skipped, since the preflight would refuse before the stall.
/// </summary>
[NotInParallel(nameof(SetupLocalCancelLiveTests))]
public class SetupLocalCancelLiveTests {
    const string CheckEnvVar = "KCAP_LOCAL_SETUP_CHECK";

    static readonly TimeSpan ExitBudget = TimeSpan.FromSeconds(30);

    [TempDir("slc")] public required TempDir Tmp { get; init; }

    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }

    [Test]
    public async Task Sigterm_while_the_manifest_stalls_rolls_back_and_exits_130() {
        Skip.When(Environment.GetEnvironmentVariable(CheckEnvVar) != "1",
            $"Set {CheckEnvVar}=1 to run kcap setup --local against this machine's launchd.");
        Skip.When(!OperatingSystem.IsMacOS(), "The local edition installs on macOS.");
        Skip.When(await ProductionLabelLoadedAsync(), $"{LocalServerUnit.ProductionLabel} is already loaded for this user.");

        // Under /tmp rather than the temp root, which leaves no room in the socket-path budget.
        var root = Path.Combine("/tmp", "kls-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(root);

        using var bundles = WireMockServer.Start();
        bundles.Given(Request.Create().UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithDelay(TimeSpan.FromSeconds(60)));

        try {
            var psi = KcapProcess.StartInfo(Daemons.Store, new ConfigRoot(Tmp.CreateDir("config")),
                "setup", "--local", "--json", "--single-user", "--yes");
            psi.Environment["HOME"]                         = Tmp.CreateDir("home");
            psi.Environment[LocalRoot.EnvVar]               = root;
            psi.Environment[LocalBundleClient.BaseUrlEnvVar] = bundles.Url;
            psi.Environment[LocalBundleClient.VersionEnvVar] = "1.3.0-beta.10";
            psi.Environment["KCAP_TELEMETRY"]               = "0";
            psi.Environment["DO_NOT_TRACK"]                 = "1";

            using var kcap = Process.Start(psi)!;
            var stderr = kcap.StandardError.ReadToEndAsync();
            var lines  = new List<string>();

            try {
                using var exit = new CancellationTokenSource(ExitBudget);

                while (await kcap.StandardOutput.ReadLineAsync(exit.Token) is { } line) {
                    lines.Add(line);
                    if (Event(line) == "port_chosen") break;
                }

                await Assert.That(lines.Select(Event)).Contains("port_chosen");

                await Signal(kcap.Id, "TERM");

                while (await kcap.StandardOutput.ReadLineAsync(exit.Token) is { } line) lines.Add(line);
                await kcap.WaitForExitAsync(exit.Token);
            } finally {
                if (!kcap.HasExited) await Signal(kcap.Id, "KILL");
            }

            var output = string.Join('\n', lines) + "\n" + await stderr;
            using var last = JsonDocument.Parse(lines[^1]);

            await Assert.That(kcap.ExitCode).IsEqualTo(130).Because(output);
            await Assert.That(last.RootElement.GetProperty("event").GetString()).IsEqualTo("failed").Because(output);
            await Assert.That(last.RootElement.GetProperty("code").GetString()).IsEqualTo("cancelled").Because(output);
            await Assert.That(File.Exists(Path.Combine(root, "journal.json"))).IsFalse();
            await Assert.That(Path.Exists(Path.Combine(root, "bin"))).IsFalse();
            await Assert.That(await ProductionLabelLoadedAsync()).IsFalse();
        } finally {
            Directory.Delete(root, recursive: true);
        }
    }

    static string? Event(string line) {
        try {
            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.TryGetProperty("event", out var e) ? e.GetString() : null;
        } catch (JsonException) {
            return null;
        }
    }

    static async Task Signal(int pid, string signal) {
        using var kill = Process.Start(new ProcessStartInfo("/bin/kill", [$"-{signal}", pid.ToString(System.Globalization.CultureInfo.InvariantCulture)]))!;
        await kill.WaitForExitAsync();
    }

    static async Task<bool> ProductionLabelLoadedAsync() {
        var uid = (await RunAsync("/usr/bin/id", "-u")).Output.Trim();

        return (await RunAsync("/bin/launchctl", "print", $"gui/{uid}/{LocalServerUnit.ProductionLabel}")).ExitCode == 0;
    }

    static async Task<(int ExitCode, string Output)> RunAsync(string file, params string[] args) {
        var psi = new ProcessStartInfo(file, args) { RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        var output = await p.StandardOutput.ReadToEndAsync();
        await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();

        return (p.ExitCode, output);
    }
}
