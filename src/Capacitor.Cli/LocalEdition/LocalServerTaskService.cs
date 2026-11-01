using System.Globalization;
using System.Text;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.LocalEdition;

/// <summary>
/// The local server as a per-user logon Scheduled Task, <c>conhost --headless</c> running a <c>.cmd</c> wrapper that
/// runs the server. Needs no elevation, which makes it the supervisor wherever <c>kcap-service</c> is not installed or
/// cannot run the server, and the one <see cref="LocalServerWindowsService"/> falls back to.
///
/// <para>A stop is a Ctrl+Break on the task's console, which the server takes as a graceful shutdown and Postgres,
/// on a console of its own, never sees. <c>schtasks /End</c> alone would close that console under the server and end
/// it before it stops Postgres. Ending the task ends only its conhost, never the wrapper's cmd, so a stop first leaves a
/// marker the wrapper checks before every launch.</para>
/// </summary>
sealed class LocalServerTaskService(
    UserHome home,
    TimeProvider time,
    Func<string[], (int ExitCode, string StdOut, string StdErr)>? schtasks = null,
    UnitFileWriter? writeUnit = null,
    Func<LocalRoot, string?>? findLive = null,
    Func<LocalServerUnit, int?>? findServer = null,
    Func<int, TimeSpan, string?>? breakConsole = null
) : ILocalServerService {
    /// <summary>The server's own shutdown, Postgres's included, then time for the probe to see the lock released.</summary>
    static readonly TimeSpan StopBudget = TimeSpan.FromSeconds(90);

    static readonly TimeSpan BreakBudget = TimeSpan.FromSeconds(15);

    /// <summary>From the stack going down to the server's process exiting; past it, the task is ended regardless.</summary>
    static readonly TimeSpan ExitBudget = TimeSpan.FromSeconds(15);

    static readonly TimeSpan ExitPollGap = TimeSpan.FromMilliseconds(250);

    /// <summary>Each schtasks call of a quick uninstall, which an app uninstaller's deadline bounds; schtasks answers in
    /// well under a second when it answers at all.</summary>
    static readonly TimeSpan QuickCommandBudget = TimeSpan.FromSeconds(2);

    readonly Func<string[], (int ExitCode, string StdOut, string StdErr)> _schtasks = schtasks ?? (args => ServiceProcess.Run("schtasks", args));
    readonly Func<string[], (int ExitCode, string StdOut, string StdErr)> _schtasksQuick = schtasks ?? (args => Bounded(args, QuickCommandBudget));
    readonly UnitFileWriter _writeUnit = writeUnit ?? ((path, content, encoding) => ServiceFiles.WriteOwnerOnly(path, content, encoding, durable: true, home: home));
    readonly Func<LocalServerUnit, int?> _findServer = findServer ?? FindTaskServer;
    readonly Func<int, TimeSpan, string?> _breakConsole = breakConsole ?? BreakThroughHelper;

    public string Describe => "Windows Scheduled Task";

    public string Name(LocalServerUnit unit) => unit.TaskName;

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
            _ when File.Exists(StopMarker(unit)) => UnitActivity.Stopped.Instance,
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
        _writeUnit(wrapper, LocalServerTaskUnit.Wrapper(unit, StopMarker(unit)), WindowsScheduledTaskServiceManager.WrapperEncoding);

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

        ClearStop(unit);
        Check(LocalServerTaskUnit.RunArgs(unit));
    }

    string StopMarker(LocalServerUnit unit) => LocalServerTaskUnit.StopMarkerPath(home, unit);

    void ClearStop(LocalServerUnit unit) {
        if (File.Exists(StopMarker(unit))) File.Delete(StopMarker(unit));
    }

    bool Running(LocalServerUnit unit) =>
        _findServer(unit) is not null
     || _schtasks(LocalServerTaskUnit.QueryArgs(unit)) is (0, var query, _) && LocalServerTaskUnit.LastResult(query) == LocalServerTaskUnit.RunningResult;

    public void Start(LocalServerUnit unit) {
        ClearStop(unit);
        Check(LocalServerTaskUnit.EnableArgs(unit));
        Check(LocalServerTaskUnit.RunArgs(unit));
    }

    public async Task StopAsync(LocalServerUnit unit, CancellationToken ct) {
        var (exit, _, err) = _schtasks(LocalServerTaskUnit.DisableArgs(unit));
        if (exit != 0 && !ConfirmedAbsent(unit)) throw Failed(LocalServerTaskUnit.DisableArgs(unit), exit, err);

        await Stop(unit, ct);
    }

    /// <summary>Marks the stop so the wrapper launches nothing more, breaks the server, waits for the stack to go down,
    /// then ends the task's console.</summary>
    async Task Stop(LocalServerUnit unit, CancellationToken ct) {
        _writeUnit(StopMarker(unit), "", null);

        if (_findServer(unit) is { } pid && _breakConsole(pid, BreakBudget) is { } error)
            throw new LocalLifecycleException($"The local server (pid {pid}) could not be asked to stop: {error}");

        await LocalStackProbe.WaitUntilDownAsync(unit.Root, StopBudget, time, ct, findLive);

        // The server lets go of the root before its process exits, and ending the task under it would cut that short.
        var deadline = time.GetUtcNow() + ExitBudget;
        while (_findServer(unit) is not null && time.GetUtcNow() < deadline) await Task.Delay(ExitPollGap, time, ct);

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

        foreach (var file in new[] { LocalServerTaskUnit.WrapperPath(home, unit), StopMarker(unit) }) {
            if (!File.Exists(file)) continue;

            File.Delete(file);
            FaultPoints.Hit($"unlink:{file}");
        }
    }

    /// <summary>The stop marker stays while anything may still run: the wrapper's cmd outlives an ended task and would
    /// launch the server again once the one running exits.</summary>
    public async Task UninstallWithinAsync(LocalServerUnit unit, TimeSpan grace, CancellationToken ct) {
        var live     = findLive ?? LocalStackProbe.FindLive;
        var deadline = time.GetUtcNow() + grace;

        _schtasksQuick(LocalServerTaskUnit.DisableArgs(unit));
        _writeUnit(StopMarker(unit), "", null);

        // The sender's wait comes out of the grace too, or a hung sender would outlast the caller's deadline.
        if (_findServer(unit) is { } pid && deadline - time.GetUtcNow() is { Ticks: > 0 } left) _breakConsole(pid, left);

        while (live(unit.Root) is not null && time.GetUtcNow() < deadline) await Task.Delay(ExitPollGap, time, ct);

        // Past the grace, /End and /Delete are still tried; one that times out fails the uninstall with the marker kept.
        // /End also fails for a task that is not running, so its failure only keeps the marker: a wrapper it could not
        // end still checks the marker before launching again, and the next install clears it.
        var ended = _schtasksQuick(LocalServerTaskUnit.EndArgs(unit)).ExitCode == 0;

        // A Task Scheduler that timed out answering the delete would stall the absence check too.
        var (exit, _, err) = _schtasksQuick(LocalServerTaskUnit.DeleteArgs(unit));
        if (exit != 0 && (exit == TimedOutExit || !ConfirmedAbsent(unit))) throw Failed(LocalServerTaskUnit.DeleteArgs(unit), exit, err);

        if (File.Exists(LocalServerTaskUnit.WrapperPath(home, unit))) File.Delete(LocalServerTaskUnit.WrapperPath(home, unit));
        if (ended && live(unit.Root) is null && _findServer(unit) is null) ClearStop(unit);
    }

    const int TimedOutExit = -1;

    static (int ExitCode, string StdOut, string StdErr) Bounded(string[] args, TimeSpan budget) {
        var (exit, stdout, stderr, timedOut) = ServiceProcess.RunBounded("schtasks", args, budget);
        return timedOut ? (TimedOutExit, stdout, $"schtasks did not answer within {budget.TotalSeconds:0}s") : (exit, stdout, stderr);
    }

    void Check(string[] args) {
        var (exit, _, err) = _schtasks(args);
        if (exit != 0) throw Failed(args, exit, err);
    }

    static LocalLifecycleException Failed(string[] args, int exit, string err) =>
        new($"schtasks {string.Join(' ', args)} failed (exit {exit}): {err.Trim()}");

    /// <summary>The server the task runs: this root's binary under the wrapper's cmd, under the task's conhost. A server
    /// started by hand has neither above it, and one serving another root runs another binary.</summary>
    /// <remarks>The OS reports an image path with links resolved, so <c>bin\current</c> never appears in it: the image
    /// is this unit's binary resolved, or any version under this root's <c>bin</c>, which a server started before an
    /// upgrade moved <c>current</c> still runs.</remarks>
    internal static int? TaskServer(IReadOnlyDictionary<int, WindowsProcessEntry> table, Func<int, string?> imagePath, string serverBinary) {
        var expected = CanonicalPath.Resolve(serverBinary);
        var bin      = Path.GetDirectoryName(Path.GetDirectoryName(serverBinary))!;

        foreach (var (pid, entry) in table) {
            if (!IsImage(entry, Path.GetFileName(serverBinary))
             || !table.TryGetValue(entry.ParentPid, out var wrapper) || !IsImage(wrapper, "cmd.exe")
             || !table.TryGetValue(wrapper.ParentPid, out var host) || !IsImage(host, "conhost.exe"))
                continue;

            if (imagePath(pid) is { } path && (PathComparison.Equal(CanonicalPath.Resolve(path), expected) || CanonicalPath.IsWithin(path, bin)))
                return pid;
        }

        return null;
    }

    static bool IsImage(WindowsProcessEntry entry, string exe) => string.Equals(entry.ExeName, exe, StringComparison.OrdinalIgnoreCase);

    static int? FindTaskServer(LocalServerUnit unit) =>
        OperatingSystem.IsWindows() ? TaskServer(WindowsProcessTable.Snapshot(), WindowsProcessTable.ImagePath, unit.ServerBinary) : null;

    /// <summary>The sender leaves its own console for the server's, so it is a child, never this process: the command
    /// running the stop still has a console to report on.</summary>
    static string? BreakThroughHelper(int pid, TimeSpan budget) {
        if (Environment.ProcessPath is not { } kcap) return "the kcap executable's path is unknown";

        var (exit, _, err, timedOut) = ServiceProcess.RunBounded(kcap,
            ["local", LocalConsoleBreakCommand.Name, pid.ToString(CultureInfo.InvariantCulture)], budget);

        return timedOut ? $"the sender did not finish within {budget.TotalSeconds:0}s"
             : exit == 0 ? null
             : string.IsNullOrWhiteSpace(err) ? $"the sender exited {exit}" : err.Trim();
    }
}
