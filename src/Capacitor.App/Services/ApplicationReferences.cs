using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.Setup;

namespace Capacitor.App.Services;

public interface IApplicationReferences : IAsyncDisposable {
    Task PrepareAsync(IReadOnlyList<string> oldRoots, string target, CancellationToken ct);
    Task ApplyAsync(CancellationToken ct);
    Task RollbackAsync(Action restoreBundles);
}

internal sealed partial class ApplicationReferences(IProcessRunner runner, UserHome home, DaemonStore store, TimeProvider time,
        Func<string, CancellationToken, Task<bool>>? keepAgents = null,
        Func<string, CancellationToken, Task<bool>>? ready = null,
        string? domainOverride = null, IReadOnlyList<string>? shimFolders = null) : IApplicationReferences {
    static readonly string[] Binaries = ["kcap", "kcap-daemon"];
    sealed class Unit(string path, string name, string original, string replacement, bool loaded) {
        public string Path = path, Name = name, Original = original, Replacement = replacement;
        public bool Loaded = loaded, Stopped, Changed, Started;
    }
    sealed class Link(string path, string original, string replacement) {
        public string Path = path, Original = original, Replacement = replacement;
        public bool Changed;
    }
    readonly List<Unit> _units = [];
    readonly List<Link> _links = [];
    readonly List<FileStream> _locks = [];
    string Domain => domainOverride ?? $"gui/{getuid()}";

    [LibraryImport("libc", EntryPoint = "getuid")]
    private static partial uint getuid();

    public async Task PrepareAsync(IReadOnlyList<string> oldRoots, string target, CancellationToken ct) {
        if (oldRoots.Count == 0) return;
        var replacements = oldRoots.Distinct().SelectMany(root => Binaries
            .Select(binary => (Old: Path.Combine(root, "Contents", "MacOS", binary), New: Path.Combine(target, "Contents", "MacOS", binary))))
            .ToDictionary(pair => Path.GetFullPath(pair.Old), pair => pair.New, StringComparer.Ordinal);
        var agents = Path.Combine(home.Path, "Library", "LaunchAgents");
        if (Directory.Exists(agents)) {
            foreach (var path in Directory.EnumerateFiles(agents, "io.kurrent.kcap.daemon.*.plist").Order()) {
                if (new FileInfo(path).LinkTarget is not null) throw new IOException($"Cannot migrate a linked service file: {path}");
                var original = File.ReadAllText(path);
                using var reader = XmlReader.Create(new StringReader(original), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
                var doc = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
                var entries = doc.Root?.Element("dict")?.Elements().ToArray() ?? [];
                XElement? Value(string key) => entries.Select((element, i) => (element, i))
                    .Where(pair => pair.element.Name == "key" && pair.element.Value == key && pair.i + 1 < entries.Length)
                    .Select(pair => entries[pair.i + 1]).SingleOrDefault();
                var args = Value("ProgramArguments");
                var binary = args?.Elements("string").FirstOrDefault();
                if (binary is null || !replacements.TryGetValue(binary.Value, out var replacement)) continue;
                var label = Value("Label")?.Value;
                const string prefix = "io.kurrent.kcap.daemon.";
                if (label is null || !label.StartsWith(prefix, StringComparison.Ordinal)) throw new IOException($"Invalid service label in {path}");
                var name = label[prefix.Length..];
                if (name != DaemonStore.Sanitize(name) || Path.GetFileName(path) != label + ".plist") throw new IOException($"Invalid service name in {path}");
                store.EnsureDirectory();
                _locks.Add(new FileStream(store.ServiceLockPath(name), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
                if (File.ReadAllText(path) != original) throw new IOException($"The service changed while preparing: {name}");
                var status = await PrintAsync(name, ct);
                var changed = binary.Value != replacement;
                binary.Value = replacement;
                var unit = new Unit(path, name, original, changed ? doc.ToString(SaveOptions.DisableFormatting) : original, status.ExitCode == 0);
                _units.Add(unit);
                if (!unit.Loaded) continue;
                unit.Stopped = true;
                if (status.Stdout.Contains("pid = ", StringComparison.Ordinal)
                        && !await (keepAgents?.Invoke(name, ct) ?? KeepAgentsAsync(name, ct)))
                    throw new IOException($"Daemon '{name}' did not accept keeping its agents. Nothing will be replaced; finish its sessions and retry.");
                // Even a timed-out bootout may have unloaded the job: retain it for rollback.
                await CtlAsync(["bootout", $"{Domain}/io.kurrent.kcap.daemon.{name}"], ct);
                var after = await PrintAsync(name, ct);
                if (after.ExitCode == 0) throw new IOException($"Daemon '{name}' is still loaded.");
            }
        }
        foreach (var folder in shimFolders ?? KcapLinkFolders.For(home)) {
            var path = Path.Combine(folder, "kcap");
            var info = new FileInfo(path);
            if (info.LinkTarget is not { } original) continue;
            var resolved = info.ResolveLinkTarget(true)?.FullName;
            if (resolved is not null && replacements.TryGetValue(resolved, out var replacement) && resolved != replacement)
                _links.Add(new Link(path, original, replacement));
        }
    }

    public async Task ApplyAsync(CancellationToken ct) {
        foreach (var unit in _units) {
            if (File.ReadAllText(unit.Path) != unit.Original) throw new IOException($"Service '{unit.Name}' changed during installation.");
            if (unit.Original != unit.Replacement) {
                WriteUnit(unit.Path, unit.Replacement);
                unit.Changed = true;
            }
        }
        foreach (var link in _links) {
            await ReplaceLinkAsync(link.Path, link.Original, link.Replacement, ct);
            link.Changed = true;
        }
        foreach (var unit in _units.Where(unit => unit.Loaded)) await StartAsync(unit, ct);
    }

    public async Task RollbackAsync(Action restoreBundles) {
        foreach (var unit in _units.Where(unit => unit.Started)) {
            var status = await PrintAsync(unit.Name, CancellationToken.None);
            if (status.ExitCode == 0) {
                if (status.Stdout.Contains("pid = ", StringComparison.Ordinal)
                        && !await (keepAgents?.Invoke(unit.Name, CancellationToken.None) ?? KeepAgentsAsync(unit.Name, CancellationToken.None)))
                    throw new IOException($"Daemon '{unit.Name}' could not be stopped for rollback. The new bundle and its backups have been retained.");
                await CtlAsync(["bootout", $"{Domain}/io.kurrent.kcap.daemon.{unit.Name}"], CancellationToken.None);
                if ((await PrintAsync(unit.Name, CancellationToken.None)).ExitCode == 0)
                    throw new IOException($"Daemon '{unit.Name}' is still loaded; installation backups have been retained.");
            }
        }
        restoreBundles();
        var errors = new List<string>();
        var restored = new List<Unit>();
        foreach (var unit in _units.AsEnumerable().Reverse()) {
            try {
                if (unit.Changed) {
                    if (File.ReadAllText(unit.Path) != unit.Replacement) throw new IOException($"Service '{unit.Name}' changed; restore {unit.Path} manually.");
                    WriteUnit(unit.Path, unit.Original);
                } else if (File.ReadAllText(unit.Path) != unit.Original) {
                    throw new IOException($"Service '{unit.Name}' was changed by another process; it was left untouched.");
                }
                restored.Add(unit);
            } catch (Exception ex) { errors.Add(ex.Message); }
        }
        foreach (var link in _links.Where(link => link.Changed).Reverse()) {
            try { await ReplaceLinkAsync(link.Path, link.Replacement, link.Original, CancellationToken.None); }
            catch (Exception ex) { errors.Add(ex.Message); }
        }
        foreach (var unit in restored.Where(unit => unit.Stopped)) {
            try {
                var status = await PrintAsync(unit.Name, CancellationToken.None);
                if (status.ExitCode != 0) await StartAsync(unit, CancellationToken.None);
                else if (!status.Stdout.Contains("pid = ", StringComparison.Ordinal)) {
                    var start = await CtlAsync(["kickstart", $"{Domain}/io.kurrent.kcap.daemon.{unit.Name}"], CancellationToken.None);
                    if (start.ExitCode != 0 || start.TimedOut || !await (ready?.Invoke(unit.Name, CancellationToken.None) ?? ReadyAsync(unit.Name, CancellationToken.None)))
                        throw new IOException($"Could not restart daemon '{unit.Name}'.");
                }
            } catch (Exception ex) { errors.Add(ex.Message); }
        }
        if (errors.Count > 0) throw new IOException("Could not fully restore installation references: " + string.Join(" ", errors));
    }

    async Task StartAsync(Unit unit, CancellationToken ct) {
        unit.Started = true;
        var result = await CtlAsync(["bootstrap", Domain, unit.Path], ct);
        if (result.ExitCode != 0 || result.TimedOut) throw new IOException($"Could not start daemon '{unit.Name}': {result.Stderr}");
        if (!await (ready?.Invoke(unit.Name, ct) ?? ReadyAsync(unit.Name, ct)))
            throw new IOException($"Daemon '{unit.Name}' did not become ready.");
    }

    async Task<ProcessResult> PrintAsync(string name, CancellationToken ct) {
        var result = await CtlAsync(["print", $"{Domain}/io.kurrent.kcap.daemon.{name}"], ct);
        if (result.TimedOut || (result.ExitCode != 0 && !result.Stderr.Contains("Could not find service", StringComparison.OrdinalIgnoreCase)))
            throw new IOException($"Could not determine daemon '{name}' service state.");
        return result;
    }

    Task<ProcessResult> CtlAsync(string[] args, CancellationToken ct) => runner.RunAsync("/bin/launchctl", args,
        new RunOptions(Timeout: TimeSpan.FromSeconds(10), CancelMode: CancelMode.KillTree), ct);

    async Task<bool> KeepAgentsAsync(string name, CancellationToken ct) {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25), time);
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        try {
            var reply = await ExchangeAsync(name, LocalFrame.Shutdown(new ShutdownRequestDto("keep", "final")), bound.Token);
            if (reply is not { Type: FrameType.ShutdownAck, Text: "stopping" or "queued" }) return false;
            while (File.Exists(store.PidPath(name))) await Task.Delay(TimeSpan.FromMilliseconds(100), time, bound.Token);
            return true;
        } catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
    }

    async Task<bool> ReadyAsync(string name, CancellationToken ct) {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20), time);
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        while (!bound.IsCancellationRequested) {
            try {
                var reply = await ExchangeAsync(name, new LocalFrame(FrameType.Hello), bound.Token);
                if (reply is { Type: FrameType.HelloReply, Text: { } json }
                        && JsonSerializer.Deserialize(json, HelloIpcJsonContext.Default.HelloReplyDto) is { } hello
                        && hello.DaemonName == name && hello.ProtocolVersion == HelloProtocol.CurrentVersion) return true;
            } catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
            catch (Exception ex) when (ex is SocketException or IOException or JsonException) { }
            try { await Task.Delay(TimeSpan.FromMilliseconds(100), time, bound.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
        }
        return false;
    }

    async Task<LocalFrame?> ExchangeAsync(string name, LocalFrame request, CancellationToken ct) {
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(store.SocketPath(name)), ct);
        await using var stream = new NetworkStream(socket, false);
        await FrameCodec.WriteAsync(stream, request, ct);
        return await FrameCodec.ReadAsync(stream, ct);
    }

    static void WriteUnit(string path, string text) {
        var staging = path + ".migrating-" + Guid.NewGuid().ToString("N");
        try {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var file = new FileStream(staging, options))
            {
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(file.SafeFileHandle, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                using var writer = new StreamWriter(file);
                writer.Write(text);
            }
            File.Move(staging, path, true);
        } finally { if (File.Exists(staging)) File.Delete(staging); }
    }

    async Task ReplaceLinkAsync(string path, string expected, string replacement, CancellationToken ct) {
        if (new FileInfo(path).LinkTarget != expected) throw new IOException($"The terminal link changed: {path}");
        var staging = path + ".migrating-" + Guid.NewGuid().ToString("N");
        try {
            File.CreateSymbolicLink(staging, replacement);
            if (new FileInfo(path).LinkTarget != expected) throw new IOException($"The terminal link changed: {path}");
            File.Move(staging, path, true);
        } catch (UnauthorizedAccessException) {
            const string script = "on run argv\nset p to quoted form of item 1 of argv\nset old to quoted form of item 2 of argv\nset dest to quoted form of item 3 of argv\nset tmp to quoted form of item 4 of argv\ndo shell script \"test -L \" & p & \" && test \\\"$(/usr/bin/readlink \" & p & \")\\\" = \" & old & \" && /bin/ln -s \" & dest & \" \" & tmp & \" && /bin/mv -f \" & tmp & \" \" & p with administrator privileges\nend run";
            var result = await runner.RunAsync("/usr/bin/osascript", ["-e", script, "--", path, expected, replacement, staging],
                new RunOptions(Timeout: TimeSpan.FromMinutes(2)), ct);
            if (result.ExitCode != 0 || result.TimedOut || new FileInfo(path).LinkTarget != replacement)
                throw new IOException("The terminal link could not be migrated. Administrator approval may be required.");
        } finally { CleanupLink(staging, File.Delete); }
    }

    internal static void CleanupLink(string staging, Action<string> delete) {
        try { if (new FileInfo(staging).LinkTarget is not null) delete(staging); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            Console.Error.WriteLine($"kcap: could not remove temporary terminal link {staging}: {ex.Message}");
        }
    }

    public ValueTask DisposeAsync() {
        foreach (var file in _locks) file.Dispose();
        return ValueTask.CompletedTask;
    }
}
