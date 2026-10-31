using System.Globalization;
using System.Text;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.LocalEdition;

/// <summary>
/// The local server as a per-user logon Scheduled Task, <c>conhost --headless</c> running a <c>.cmd</c> wrapper that
/// runs the server. No elevation: the machine's <c>kcap-service</c> supervises daemons only.
///
/// <para>A stop is a Ctrl+Break on the task's console, which the server takes as a graceful shutdown and Postgres,
/// on a console of its own, never sees. <c>schtasks /End</c> alone would close that console under the server and end
/// it before it stops Postgres. cmd outlives the break, so the task is ended once the stack is down.</para>
/// </summary>
sealed class LocalServerTaskService(
    UserHome home,
    TimeProvider time,
    Func<string[], (int ExitCode, string StdOut, string StdErr)>? schtasks = null,
    UnitFileWriter? writeUnit = null,
    Func<LocalRoot, string?>? findLive = null,
    Func<LocalServerUnit, int?>? findServer = null,
    Func<int, string?>? breakConsole = null
) : ILocalServerService {
    /// <summary>The server's own shutdown, Postgres's included, then time for the probe to see the lock released.</summary>
    static readonly TimeSpan StopBudget = TimeSpan.FromSeconds(90);

    static readonly TimeSpan BreakBudget = TimeSpan.FromSeconds(15);

    readonly Func<string[], (int ExitCode, string StdOut, string StdErr)> _schtasks = schtasks ?? (args => ServiceProcess.Run("schtasks", args));
    readonly UnitFileWriter _writeUnit = writeUnit ?? ((path, content, encoding) => ServiceFiles.WriteOwnerOnly(path, content, encoding, durable: true, home: home));
    readonly Func<LocalServerUnit, int?> _findServer = findServer ?? FindTaskServer;
    readonly Func<int, string?> _breakConsole = breakConsole ?? BreakThroughHelper;

    public string Describe => "Windows Scheduled Task";

    public string UnitFile(LocalServerUnit unit) => LocalServerTaskUnit.WrapperPath(home, unit);

    public bool IsInstalled(LocalServerUnit unit) => File.Exists(UnitFile(unit)) && _schtasks(LocalServerTaskUnit.QueryArgs(unit)).ExitCode == 0;

    public UnitOwner Owner(LocalServerUnit unit) => new(LoadedOwner(unit), WrapperOwner(UnitFile(unit)));

    OwnerReading LoadedOwner(LocalServerUnit unit) {
        var (exit, xml, err) = _schtasks(LocalServerTaskUnit.QueryXmlArgs(unit));
        if (exit != 0)
            return ConfirmedAbsent(unit)
                ? OwnerReading.Absent.Instance
                : new OwnerReading.Unreadable($"schtasks /Query of {unit.TaskName} failed (exit {exit}): {err.Trim()}");

        if (WindowsTaskUnit.WrapperFromTaskXml(xml) is not { } wrapper)
            return new OwnerReading.Unreadable($"the task {unit.TaskName} runs an action kcap did not write");

        var reading = WrapperOwner(wrapper);
        return reading is OwnerReading.Absent ? new OwnerReading.Unreadable($"the task runs {wrapper}, which is missing") : reading;
    }

    static OwnerReading WrapperOwner(string path) {
        string text;

        try {
            text = File.ReadAllText(path);
        } catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) {
            return OwnerReading.Absent.Instance;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return new OwnerReading.Unreadable($"{path} could not be read");
        }

        return WindowsTaskUnit.EnvFromWrapper(text).TryGetValue(LocalRoot.EnvVar, out var root)
            ? new OwnerReading.Root(root)
            : new OwnerReading.Unreadable($"{path} names no {LocalRoot.EnvVar}");
    }

    /// <summary>A failed query of one task proves nothing: only a listing that succeeds and leaves it out says it is gone.</summary>
    bool ConfirmedAbsent(LocalServerUnit unit) {
        var (exit, listing, _) = _schtasks(LocalServerTaskUnit.ListArgs());
        return exit == 0 && !LocalServerTaskUnit.Listed(listing, unit);
    }

    public UnitActivity Activity(LocalServerUnit unit) {
        if (_findServer(unit) is { } pid) return new UnitActivity.Running(pid);

        var (exit, query, err) = _schtasks(LocalServerTaskUnit.QueryArgs(unit));
        if (exit != 0)
            return ConfirmedAbsent(unit)
                ? UnitActivity.Stopped.Instance
                : new UnitActivity.Unknown($"schtasks /Query of {unit.TaskName} failed (exit {exit}): {err.Trim()}");

        return LocalServerTaskUnit.LastResult(query) switch {
            // The wrapper is up and the server is not: between a failure and its relaunch, or not yet started.
            LocalServerTaskUnit.RunningResult => new UnitActivity.Restarting(null),
            null or LocalServerTaskUnit.NeverRunResult or LocalServerTaskUnit.TerminatedResult => UnitActivity.Stopped.Instance,
            var result => new UnitActivity.Exited(result),
        };
    }

    public void Install(LocalServerUnit unit) {
        unit.PrepareLogDirectory();

        // cmd reads a running batch file by offset, so rewriting it under a running task would run a mix of both.
        if (Running(unit)) Stop(unit, CancellationToken.None).GetAwaiter().GetResult();

        var wrapper  = LocalServerTaskUnit.WrapperPath(home, unit);
        var previous = File.Exists(wrapper) ? File.ReadAllText(wrapper) : null;
        _writeUnit(wrapper, LocalServerTaskUnit.Wrapper(unit), WindowsScheduledTaskServiceManager.WrapperEncoding);

        var xml = LocalServerTaskUnit.XmlPath(home, unit);
        _writeUnit(xml, LocalServerTaskUnit.TaskXml(unit, wrapper), Encoding.Unicode);

        var (exit, _, err) = _schtasks(LocalServerTaskUnit.CreateArgs(unit, xml));
        File.Delete(xml);

        if (exit != 0) {
            // An existing task runs whatever its wrapper says at the next logon, so a refused one must not leave it repointed.
            if (previous is null) File.Delete(wrapper);
            else _writeUnit(wrapper, previous, WindowsScheduledTaskServiceManager.WrapperEncoding);

            throw Failed(LocalServerTaskUnit.CreateArgs(unit, xml), exit, err);
        }

        Check(LocalServerTaskUnit.RunArgs(unit));
    }

    bool Running(LocalServerUnit unit) =>
        _findServer(unit) is not null
     || _schtasks(LocalServerTaskUnit.QueryArgs(unit)) is (0, var query, _) && LocalServerTaskUnit.LastResult(query) == LocalServerTaskUnit.RunningResult;

    public void Start(LocalServerUnit unit) {
        Check(LocalServerTaskUnit.EnableArgs(unit));
        Check(LocalServerTaskUnit.RunArgs(unit));
    }

    public async Task StopAsync(LocalServerUnit unit, CancellationToken ct) {
        var (exit, _, err) = _schtasks(LocalServerTaskUnit.DisableArgs(unit));
        if (exit != 0 && !ConfirmedAbsent(unit)) throw Failed(LocalServerTaskUnit.DisableArgs(unit), exit, err);

        await Stop(unit, ct);
    }

    /// <summary>Breaks the server, waits for the stack to go down, then ends whatever the task still runs: the wrapper
    /// after a break, or one pausing before a relaunch.</summary>
    async Task Stop(LocalServerUnit unit, CancellationToken ct) {
        if (_findServer(unit) is { } pid && _breakConsole(pid) is { } error)
            throw new LocalLifecycleException($"The local server (pid {pid}) could not be asked to stop: {error}");

        await LocalStackProbe.WaitUntilDownAsync(unit.Root, StopBudget, time, ct, findLive);

        var (exit, _, err) = _schtasks(LocalServerTaskUnit.EndArgs(unit));
        // /End fails for a task that is not running; only one still running, or one that cannot be read, is a failure.
        if (exit != 0 && Running(unit) && !ConfirmedAbsent(unit)) throw Failed(LocalServerTaskUnit.EndArgs(unit), exit, err);

        // A relaunch that beat the end would have taken the root again.
        await LocalStackProbe.WaitUntilDownAsync(unit.Root, StopBudget, time, ct, findLive);
    }

    public async Task UninstallAsync(LocalServerUnit unit, CancellationToken ct) {
        await StopAsync(unit, ct);

        var (exit, _, err) = _schtasks(LocalServerTaskUnit.DeleteArgs(unit));
        if (exit != 0 && !ConfirmedAbsent(unit)) throw Failed(LocalServerTaskUnit.DeleteArgs(unit), exit, err);

        var wrapper = LocalServerTaskUnit.WrapperPath(home, unit);
        if (File.Exists(wrapper)) {
            File.Delete(wrapper);
            FaultPoints.Hit($"unlink:{wrapper}");
        }
    }

    void Check(string[] args) {
        var (exit, _, err) = _schtasks(args);
        if (exit != 0) throw Failed(args, exit, err);
    }

    static LocalLifecycleException Failed(string[] args, int exit, string err) =>
        new($"schtasks {string.Join(' ', args)} failed (exit {exit}): {err.Trim()}");

    /// <summary>The server the task runs: this unit's binary under the wrapper's cmd, under the task's conhost. A server
    /// started by hand has neither above it, and one serving another root runs another binary.</summary>
    internal static int? TaskServer(IReadOnlyDictionary<int, WindowsProcessEntry> table, Func<int, string?> imagePath, string serverBinary) {
        var expected = Path.GetFullPath(serverBinary);

        foreach (var (pid, entry) in table) {
            if (!IsImage(entry, Path.GetFileName(expected))
             || !table.TryGetValue(entry.ParentPid, out var wrapper) || !IsImage(wrapper, "cmd.exe")
             || !table.TryGetValue(wrapper.ParentPid, out var host) || !IsImage(host, "conhost.exe"))
                continue;

            if (imagePath(pid) is { } path && string.Equals(Path.GetFullPath(path), expected, StringComparison.OrdinalIgnoreCase)) return pid;
        }

        return null;
    }

    static bool IsImage(WindowsProcessEntry entry, string exe) => string.Equals(entry.ExeName, exe, StringComparison.OrdinalIgnoreCase);

    static int? FindTaskServer(LocalServerUnit unit) =>
        OperatingSystem.IsWindows() ? TaskServer(WindowsProcessTable.Snapshot(), WindowsProcessTable.ImagePath, unit.ServerBinary) : null;

    /// <summary>The sender leaves its own console for the server's, so it is a child, never this process: the command
    /// running the stop still has a console to report on.</summary>
    static string? BreakThroughHelper(int pid) {
        if (Environment.ProcessPath is not { } kcap) return "the kcap executable's path is unknown";

        var (exit, _, err, timedOut) = ServiceProcess.RunBounded(kcap,
            ["local", LocalConsoleBreakCommand.Name, pid.ToString(CultureInfo.InvariantCulture)], BreakBudget);

        return timedOut ? $"the sender did not finish within {BreakBudget.TotalSeconds:0}s"
             : exit == 0 ? null
             : string.IsNullOrWhiteSpace(err) ? $"the sender exited {exit}" : err.Trim();
    }
}
