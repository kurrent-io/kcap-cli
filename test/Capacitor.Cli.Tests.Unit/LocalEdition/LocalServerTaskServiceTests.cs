using System.Text;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.LocalEdition;
using Capacitor.Cli.Services;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions.Enums;

namespace Capacitor.Cli.Tests.Unit.LocalEdition;

public class LocalServerTaskServiceTests {
    [TempDir] public required TempDir Tmp { get; init; }

    UserHome Home => new(Tmp.PathTo("home"));

    LocalServerUnit Unit => field ??= LocalServerUnit.ForInstallation(new LocalRoot(Tmp.PathTo("local")), 51234);

    string WrapperPath => LocalServerTaskUnit.WrapperPath(Home, Unit);

    /// <summary>Records every schtasks call; a call not given a result succeeds, except that the task is not
    /// registered: its queries fail and the listing leaves it out.</summary>
    sealed class Schtasks {
        public List<string> Calls { get; } = [];
        public Dictionary<string, (int, string, string)> Results { get; } = [];

        public (int, string, string) Run(string[] args) {
            var call = string.Join(' ', args);
            Calls.Add(call);

            foreach (var (prefix, result) in Results)
                if (call.StartsWith(prefix, StringComparison.Ordinal)) return result;

            return call.StartsWith("/Query /TN", StringComparison.Ordinal) ? (1, "", "ERROR: The system cannot find the file specified.") : (0, "", "");
        }
    }

    static string Row(int lastResult) => $"\"HOST\",\"\\kcap-local\",\"N/A\",\"Ready\",\"Interactive only\",\"N/A\",\"{lastResult}\",\"u\"\r\n";

    static void Write(string path, string content, Encoding? encoding = null) {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, encoding ?? Encoding.UTF8);
    }

    LocalServerTaskService Service(Schtasks schtasks, Func<LocalServerUnit, int?>? findServer = null, Func<int, string?>? breakConsole = null,
        Func<LocalRoot, string?>? findLive = null) =>
        new(Home, TimeProvider.System, schtasks.Run, Write, findLive ?? (_ => null), findServer ?? (_ => null), breakConsole ?? (_ => null));

    [Test]
    public async Task A_fresh_install_registers_the_task_and_runs_it() {
        var schtasks = new Schtasks();

        Service(schtasks).Install(Unit);

        await Assert.That(schtasks.Calls).IsEquivalentTo(new[] {
            "/Query /TN kcap-local /V /FO CSV /NH",
            $"/Create /TN kcap-local /XML {LocalServerTaskUnit.XmlPath(Home, Unit)} /F",
            "/Run /TN kcap-local",
        }, CollectionOrdering.Matching);
        await Assert.That(File.ReadAllText(WrapperPath)).IsEqualTo(LocalServerTaskUnit.Wrapper(Unit));
        await Assert.That(File.Exists(LocalServerTaskUnit.XmlPath(Home, Unit))).IsFalse();
    }

    /// <summary>cmd reads a batch file by offset while it runs, so the wrapper is never rewritten under it.</summary>
    [Test]
    public async Task Reinstalling_over_a_running_server_stops_it_before_the_wrapper_is_rewritten() {
        var schtasks = new Schtasks();
        var server   = (int?)4242;
        var order    = new List<string>();

        var service = new LocalServerTaskService(Home, TimeProvider.System, args => {
                order.Add(string.Join(' ', args.Take(1)));
                return schtasks.Run(args);
            }, (path, content, encoding) => {
                order.Add($"write {Path.GetExtension(path)}");
                Write(path, content, encoding);
            }, _ => null, _ => server, pid => {
                order.Add($"break {pid}");
                server = null;
                return null;
            });

        service.Install(Unit);

        await Assert.That(order).IsEquivalentTo(new[] { "break 4242", "/End", "write .cmd", "write .xml", "/Create", "/Run" },
            CollectionOrdering.Matching);
    }

    [Test]
    public async Task A_refused_registration_puts_the_previous_wrapper_back() {
        var schtasks = new Schtasks { Results = { ["/Create"] = (1, "", "ERROR: Access is denied.") } };
        Write(WrapperPath, "previous");

        await Assert.That(() => Service(schtasks).Install(Unit)).Throws<LocalLifecycleException>().WithMessageContaining("Access is denied");
        await Assert.That(File.ReadAllText(WrapperPath)).IsEqualTo("previous");
    }

    [Test]
    public async Task A_stop_disables_the_task_breaks_the_server_and_then_ends_the_wrapper() {
        var schtasks = new Schtasks();
        var broken   = new List<int>();

        await Service(schtasks, _ => broken.Count == 0 ? 4242 : null, pid => {
            broken.Add(pid);
            return null;
        }).StopAsync(Unit, CancellationToken.None);

        await Assert.That(broken).IsEquivalentTo(new[] { 4242 });
        await Assert.That(schtasks.Calls[0]).IsEqualTo("/Change /TN kcap-local /DISABLE");
        await Assert.That(schtasks.Calls).Contains("/End /TN kcap-local");
    }

    /// <summary>Ending the task without the break would close the console under the server before it stops Postgres.</summary>
    [Test]
    public async Task A_server_that_cannot_be_asked_to_stop_is_a_failed_stop_and_the_task_is_not_ended() {
        var schtasks = new Schtasks();

        await Assert.That(() => Service(schtasks, _ => 4242, _ => "could not attach").StopAsync(Unit, CancellationToken.None))
            .Throws<LocalLifecycleException>().WithMessageContaining("could not attach");
        await Assert.That(schtasks.Calls).DoesNotContain("/End /TN kcap-local");
    }

    [Test]
    public async Task A_stack_still_up_after_the_break_is_a_failed_stop() {
        var time     = new FakeTimeProvider();
        var schtasks = new Schtasks();
        var service  = new LocalServerTaskService(Home, time, schtasks.Run, Write, _ => "the server still holds its instance lock", _ => null, _ => null);

        var stop = service.StopAsync(Unit, CancellationToken.None);
        while (!stop.IsCompleted) time.Advance(TimeSpan.FromSeconds(1));

        await Assert.That(() => stop).Throws<LocalLifecycleException>().WithMessageContaining("instance lock");
    }

    [Test]
    public async Task Uninstall_removes_the_task_and_its_wrapper() {
        var schtasks = new Schtasks();
        Write(WrapperPath, LocalServerTaskUnit.Wrapper(Unit));

        await Service(schtasks).UninstallAsync(Unit, CancellationToken.None);

        await Assert.That(schtasks.Calls).Contains("/Delete /TN kcap-local /F");
        await Assert.That(File.Exists(WrapperPath)).IsFalse();
    }

    [Test]
    public async Task The_owner_is_the_root_the_wrapper_the_task_runs_names() {
        Write(WrapperPath, LocalServerTaskUnit.Wrapper(Unit));
        var schtasks = new Schtasks { Results = { ["/Query /TN kcap-local /XML"] = (0, LocalServerTaskUnit.TaskXml(Unit, WrapperPath, @"CORP\u"), "") } };

        var owner = Service(schtasks).Owner(Unit);

        await Assert.That(owner.Loaded).IsEqualTo(new OwnerReading.Root(Unit.Root.Directory));
        await Assert.That(owner.OnDisk).IsEqualTo(new OwnerReading.Root(Unit.Root.Directory));
    }

    [Test]
    public async Task A_task_with_an_action_kcap_did_not_write_has_no_readable_owner() {
        const string xml = """
            <Task xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Actions><Exec><Command>notepad.exe</Command><Arguments>x</Arguments></Exec></Actions>
            </Task>
            """;
        var schtasks = new Schtasks { Results = { ["/Query /TN kcap-local /XML"] = (0, xml, "") } };

        await Assert.That(Service(schtasks).Owner(Unit).Loaded).IsTypeOf<OwnerReading.Unreadable>();
    }

    /// <summary>Only a listing that succeeds and leaves the task out proves it is gone.</summary>
    [Test]
    public async Task A_failed_query_reads_absent_only_when_the_listing_confirms_it() {
        var absent = Service(new Schtasks()).Owner(Unit);
        var failed = Service(new Schtasks { Results = { ["/Query /FO CSV /NH"] = (1, "", "ERROR: RPC server unavailable") } }).Owner(Unit);

        await Assert.That(absent).IsEqualTo(new UnitOwner(OwnerReading.Absent.Instance, OwnerReading.Absent.Instance));
        await Assert.That(failed.Loaded).IsTypeOf<OwnerReading.Unreadable>();
    }

    [Test]
    public async Task Activity_follows_the_server_then_the_tasks_last_result() {
        UnitActivity Read(int lastResult) =>
            Service(new Schtasks { Results = { ["/Query /TN kcap-local /V"] = (0, Row(lastResult), "") } }).Activity(Unit);

        await Assert.That(Service(new Schtasks(), _ => 4242).Activity(Unit)).IsEqualTo(new UnitActivity.Running(4242));
        await Assert.That(Read(LocalServerTaskUnit.RunningResult)).IsEqualTo(new UnitActivity.Restarting(null));
        await Assert.That(Read(LocalServerTaskUnit.TerminatedResult)).IsEqualTo(UnitActivity.Stopped.Instance);
        await Assert.That(Read(0)).IsEqualTo(new UnitActivity.Exited(0));
        await Assert.That(Service(new Schtasks()).Activity(Unit)).IsEqualTo(UnitActivity.Stopped.Instance);
    }

    [Test]
    public async Task The_task_server_is_this_units_binary_under_the_wrappers_cmd_under_conhost() {
        var binary = Unit.ServerBinary;
        var exe    = Path.GetFileName(binary);
        var other  = Tmp.PathTo("other/bin/current/" + exe);
        var table  = new Dictionary<int, WindowsProcessEntry> {
            [10] = new(1, "conhost.exe"), [11] = new(10, "cmd.exe"), [12] = new(11, exe),
            [20] = new(1, "conhost.exe"), [21] = new(20, "cmd.exe"), [22] = new(21, exe),
            [30] = new(1, "pwsh.exe"), [31] = new(30, exe),
        };
        string? Image(int pid) => pid switch { 12 => binary, 22 => other, 31 => binary, _ => null };

        await Assert.That(LocalServerTaskService.TaskServer(table, Image, binary)).IsEqualTo(12);
        await Assert.That(LocalServerTaskService.TaskServer(table.Where(p => p.Key is not (10 or 11 or 12)).ToDictionary(), Image, binary)).IsNull();
    }
}
