# Daemon Service Supervisor Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add `kcap daemon service install|uninstall|start|stop|status` that registers the daemon as a per-user OS service (launchd / systemd `--user` / Windows Scheduled Task) so it auto-restarts after an external SIGKILL (jetsam/OOM/crash).

**Architecture:** An `IServiceManager` seam in `Capacitor.Cli` with three implementations selected by `ServiceManagerFactory`. Each platform splits a **pure** unit/wrapper text + command-vector + output-parser class (`LaunchdUnit`/`SystemdUnit`/`WindowsTaskUnit`, fully unit-tested) from a **thin** side-effecting manager (`*ServiceManager`, shells out via a small `ServiceProcess` runner, not CI-tested). `DaemonCommands` dispatches the new `service` subcommand and gains service-awareness in `status`/`stop`/`doctor`.

**Tech Stack:** .NET 10, NativeAOT, TUnit. Hand-built unit strings (no reflection serialization). `System.Diagnostics.Process` for shell-outs. `System.Security.SecurityElement.Escape` for XML escaping. Existing helpers: `DaemonLockPaths.Sanitize`, `PathHelpers`, `DaemonNameResolver`, `DaemonCommands.ResolveDaemonBinary`.

**Spec:** `docs/superpowers/specs/2026-06-10-daemon-service-supervisor-design.md`

---

## File structure

New (all under `src/Capacitor.Cli/Services/`):

| File | Responsibility |
|---|---|
| `IServiceManager.cs` | Contracts: `IServiceManager`, `ServiceSpec`, `ServiceState`, `ServiceStatus`, `GeneratedFile`, `ServicePlatform` |
| `ServiceText.cs` | Pure escaping helpers (`Xml`, `CmdValue`, `SystemdValue`) + `ServiceId` derivation |
| `ServiceProcess.cs` | Tiny shell-out runner (`Run(file, args) -> (int exit, string stdout)`) |
| `LaunchdUnit.cs` | PURE: plist text, command vectors, status parse, id-from-filename |
| `LaunchdServiceManager.cs` | Thin launchd `IServiceManager` |
| `SystemdUnit.cs` | PURE: `.service` text, vectors, status parse, id-from-filename |
| `SystemdServiceManager.cs` | Thin systemd `IServiceManager` |
| `WindowsTaskUnit.cs` | PURE: Task XML + `.cmd` wrapper text, vectors, status parse, id-from-name |
| `WindowsScheduledTaskServiceManager.cs` | Thin Windows `IServiceManager` |
| `ServiceManagerFactory.cs` | `ForPlatform` / `ForCurrentOs` |
| `ServiceEnvironment.cs` | Capture `PATH`+`KCAP_*` and resolve the pinned profile name into the env dict |

Modified:

| File | Change |
|---|---|
| `src/Capacitor.Cli/Commands/DaemonCommands.cs` | `service` subcommand dispatch; build `ServiceSpec`; `status`/`stop`/`doctor` awareness |
| `src/Capacitor.Cli.Core/Resources/help-daemon.txt` | document `service` group |
| `README.md` | getting-started note + `daemon` command section |

Tests (under `test/Capacitor.Cli.Tests.Unit/Services/`): one file per pure class + factory + environment.

All new types are `internal` (the Cli assembly has `InternalsVisibleTo Capacitor.Cli.Tests.Unit`).

---

## Task 1: Contracts + escaping + id helper

**Files:**
- Create: `src/Capacitor.Cli/Services/IServiceManager.cs`
- Create: `src/Capacitor.Cli/Services/ServiceText.cs`
- Test: `test/Capacitor.Cli.Tests.Unit/Services/ServiceTextTests.cs`

- [ ] **Step 1: Write the contracts file** (no test — consumed by later tasks)

`src/Capacitor.Cli/Services/IServiceManager.cs`:
```csharp
namespace Capacitor.Cli.Services;

/// <summary>Which OS service backend a manager targets.</summary>
enum ServicePlatform { Launchd, Systemd, WindowsScheduledTask }

/// <summary>Lifecycle state of an installed service for one id.</summary>
enum ServiceState { NotInstalled, Installed, Running }

/// <summary>A file the manager writes at install time (absolute path + content).</summary>
record GeneratedFile(string Path, string Content);

/// <summary>Status plus the binary path baked into the installed unit (for doctor).</summary>
record ServiceStatus(ServiceState State, string? BinaryPath);

/// <summary>
/// Everything needed to render and register one per-user service.
/// <paramref name="ServiceId"/> is the sanitized id (see <see cref="ServiceText.ServiceId"/>)
/// used for the filename/label/instance/task AND the daemon <c>--name</c>.
/// </summary>
record ServiceSpec(
    string                              ServiceId,
    string                              DaemonBinaryPath,
    string                              LogPath,
    IReadOnlyDictionary<string, string> Environment,
    IReadOnlyList<string>               ExtraArgs);

interface IServiceManager {
    string Describe();
    IReadOnlyList<GeneratedFile> GenerateFiles(ServiceSpec spec);
    IReadOnlyList<string>        ListInstalled();
    ServiceStatus                Status(string serviceId);
    void Install(ServiceSpec spec, bool startNow);
    void Uninstall(string serviceId);
    void Start(string serviceId);
    void Stop(string serviceId);
}
```

- [ ] **Step 2: Write the failing test for `ServiceText`**

`test/Capacitor.Cli.Tests.Unit/Services/ServiceTextTests.cs`:
```csharp
using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Services;

public class ServiceTextTests {
    [Test]
    public async Task ServiceId_sanitizes_and_lowercases() {
        await Assert.That(ServiceText.ServiceId("My Laptop")).IsEqualTo("my-laptop");
    }

    [Test]
    public async Task ServiceId_is_idempotent() {
        var once = ServiceText.ServiceId("a/b c");
        await Assert.That(ServiceText.ServiceId(once)).IsEqualTo(once);
    }

    [Test]
    public async Task Xml_escapes_the_five_markup_chars() {
        await Assert.That(ServiceText.Xml("a&b<c>\"d'")).IsEqualTo("a&amp;b&lt;c&gt;&quot;d&apos;");
    }

    [Test]
    public async Task CmdValue_doubles_percent_signs() {
        await Assert.That(ServiceText.CmdValue("100%PATH%")).IsEqualTo("100%%PATH%%");
    }

    [Test]
    public async Task SystemdValue_collapses_newlines_to_spaces() {
        await Assert.That(ServiceText.SystemdValue("a\nb")).IsEqualTo("a b");
    }
}
```

- [ ] **Step 3: Run the test to verify it fails**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/ServiceTextTests/*"`
Expected: FAIL (compile error — `ServiceText` does not exist).

- [ ] **Step 4: Write `ServiceText`**

`src/Capacitor.Cli/Services/ServiceText.cs`:
```csharp
using System.Security;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Services;

/// <summary>
/// Pure text helpers for rendering service units. Each value interpolated into
/// a unit/wrapper is escaped for its target format so generators never emit
/// malformed markup, regardless of daemon name / path content.
/// </summary>
static class ServiceText {
    /// <summary>Sanitized, portable service id — reuses the daemon lock-file rule.</summary>
    public static string ServiceId(string name) => DaemonLockPaths.Sanitize(name);

    /// <summary>XML-escape for plist and Task Scheduler XML (escapes &amp; &lt; &gt; " ').</summary>
    public static string Xml(string value) => SecurityElement.Escape(value) ?? "";

    /// <summary>Escape a value for a batch <c>set "KEY=value"</c> line: percent-signs doubled.</summary>
    public static string CmdValue(string value) => value.Replace("%", "%%");

    /// <summary>Escape a systemd <c>Environment=</c>/<c>Description=</c> value: no raw newlines.</summary>
    public static string SystemdValue(string value) =>
        value.Replace("\r", " ").Replace("\n", " ");
}
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/ServiceTextTests/*"`
Expected: PASS (5 tests).

- [ ] **Step 6: Commit**

```bash
git add src/Capacitor.Cli/Services/IServiceManager.cs src/Capacitor.Cli/Services/ServiceText.cs test/Capacitor.Cli.Tests.Unit/Services/ServiceTextTests.cs
git commit -m "feat(daemon): service contracts + text-escaping helpers"
```

---

## Task 2: launchd pure unit (`LaunchdUnit`)

**Files:**
- Create: `src/Capacitor.Cli/Services/LaunchdUnit.cs`
- Test: `test/Capacitor.Cli.Tests.Unit/Services/LaunchdUnitTests.cs`

- [ ] **Step 1: Write the failing test**

`test/Capacitor.Cli.Tests.Unit/Services/LaunchdUnitTests.cs`:
```csharp
using System.Xml.Linq;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Services;

public class LaunchdUnitTests {
    static ServiceSpec Spec(string id = "laptop") => new(
        ServiceId: id,
        DaemonBinaryPath: "/opt/kcap/kcap-daemon",
        LogPath: "/home/u/.config/kcap/daemon-laptop.log",
        Environment: new Dictionary<string, string> { ["PATH"] = "/usr/bin:/bin", ["KCAP_PROFILE"] = "work" },
        ExtraArgs: ["--max-agents", "8"]);

    [Test]
    public async Task Label_is_reverse_dns_with_id() {
        await Assert.That(LaunchdUnit.Label("laptop")).IsEqualTo("io.kurrent.kcap.daemon.laptop");
    }

    [Test]
    public async Task Plist_is_well_formed_xml_and_carries_args_and_env() {
        var plist = LaunchdUnit.Plist(Spec());
        var doc   = XDocument.Parse(plist); // throws if malformed
        await Assert.That(doc).IsNotNull();
        await Assert.That(plist).Contains("<string>/opt/kcap/kcap-daemon</string>");
        await Assert.That(plist).Contains("<string>--name</string>");
        await Assert.That(plist).Contains("<string>laptop</string>");
        await Assert.That(plist).Contains("<string>--max-agents</string>");
        await Assert.That(plist).Contains("<key>PATH</key>");
        await Assert.That(plist).Contains("<key>KCAP_PROFILE</key>");
        await Assert.That(plist).Contains("<key>SuccessfulExit</key>");
    }

    [Test]
    public async Task Plist_escapes_metacharacters_in_values() {
        var spec  = Spec() with { DaemonBinaryPath = "/opt/a&b/kcap-daemon" };
        var plist = LaunchdUnit.Plist(spec);
        XDocument.Parse(plist); // must still parse
        await Assert.That(plist).Contains("/opt/a&amp;b/kcap-daemon");
    }

    [Test]
    public async Task IdFromPlistFileName_extracts_the_id() {
        await Assert.That(LaunchdUnit.IdFromPlistFileName("io.kurrent.kcap.daemon.laptop.plist"))
            .IsEqualTo("laptop");
        await Assert.That(LaunchdUnit.IdFromPlistFileName("unrelated.plist")).IsNull();
    }

    [Test]
    public async Task StatusFromPrint_maps_exit_and_state() {
        await Assert.That(LaunchdUnit.StatusFromPrint(exitCode: 1, stdout: "")).IsEqualTo(ServiceState.NotInstalled);
        await Assert.That(LaunchdUnit.StatusFromPrint(0, "state = running")).IsEqualTo(ServiceState.Running);
        await Assert.That(LaunchdUnit.StatusFromPrint(0, "state = not running")).IsEqualTo(ServiceState.Installed);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/LaunchdUnitTests/*"`
Expected: FAIL (compile error — `LaunchdUnit` does not exist).

- [ ] **Step 3: Write `LaunchdUnit`**

`src/Capacitor.Cli/Services/LaunchdUnit.cs`:
```csharp
using System.Text;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Services;

/// <summary>Pure rendering + command vectors for a per-user launchd LaunchAgent.</summary>
static class LaunchdUnit {
    const string LabelPrefix = "io.kurrent.kcap.daemon.";

    public static string Label(string id) => LabelPrefix + id;

    /// <summary>~/Library/LaunchAgents directory for the current user.</summary>
    public static string AgentsDir() =>
        Path.Combine(PathHelpers.HomeDirectory, "Library", "LaunchAgents");

    public static string PlistPath(string id) =>
        Path.Combine(AgentsDir(), Label(id) + ".plist");

    /// <summary>Separate stdout/stderr capture file (keeps the rolling --log-file uncluttered).</summary>
    static string OutLogPath(ServiceSpec spec) =>
        Path.ChangeExtension(spec.LogPath, null) + ".out.log";

    public static string Plist(ServiceSpec spec) {
        var sb = new StringBuilder();
        sb.Append("""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>

            """);
        sb.Append($"  <key>Label</key><string>{ServiceText.Xml(Label(spec.ServiceId))}</string>\n");

        sb.Append("  <key>ProgramArguments</key><array>\n");
        foreach (var arg in ProgramArguments(spec))
            sb.Append($"    <string>{ServiceText.Xml(arg)}</string>\n");
        sb.Append("  </array>\n");

        sb.Append("  <key>EnvironmentVariables</key><dict>\n");
        foreach (var (k, v) in spec.Environment)
            sb.Append($"    <key>{ServiceText.Xml(k)}</key><string>{ServiceText.Xml(v)}</string>\n");
        sb.Append("  </dict>\n");

        sb.Append("  <key>RunAtLoad</key><true/>\n");
        sb.Append("  <key>KeepAlive</key><dict><key>SuccessfulExit</key><false/></dict>\n");
        sb.Append("  <key>ProcessType</key><string>Adaptive</string>\n");
        sb.Append($"  <key>StandardOutPath</key><string>{ServiceText.Xml(OutLogPath(spec))}</string>\n");
        sb.Append($"  <key>StandardErrorPath</key><string>{ServiceText.Xml(OutLogPath(spec))}</string>\n");
        sb.Append("</dict>\n</plist>\n");
        return sb.ToString();
    }

    /// <summary>argv the agent runs: binary, pinned --name + --log-file, then extra args.</summary>
    public static IReadOnlyList<string> ProgramArguments(ServiceSpec spec) =>
        [spec.DaemonBinaryPath, "--name", spec.ServiceId, "--log-file", spec.LogPath, .. spec.ExtraArgs];

    public static string? IdFromPlistFileName(string fileName) {
        var name = Path.GetFileNameWithoutExtension(fileName); // strips .plist
        return name.StartsWith(LabelPrefix[..^1] + ".", StringComparison.Ordinal)
            ? name[LabelPrefix.Length..]
            : null;
    }

    // ── command vectors (uid passed in so these stay pure) ──
    public static string[] BootstrapArgs(int uid, string plistPath) => ["bootstrap", $"gui/{uid}", plistPath];
    public static string[] BootoutArgs(int uid, string id)          => ["bootout", $"gui/{uid}/{Label(id)}"];
    public static string[] KickstartArgs(int uid, string id)        => ["kickstart", $"gui/{uid}/{Label(id)}"];
    public static string[] KillArgs(int uid, string id)             => ["kill", "SIGTERM", $"gui/{uid}/{Label(id)}"];
    public static string[] PrintArgs(int uid, string id)            => ["print", $"gui/{uid}/{Label(id)}"];

    public static ServiceState StatusFromPrint(int exitCode, string stdout) {
        if (exitCode != 0) return ServiceState.NotInstalled;
        return stdout.Contains("state = running", StringComparison.OrdinalIgnoreCase)
            ? ServiceState.Running
            : ServiceState.Installed;
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/LaunchdUnitTests/*"`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli/Services/LaunchdUnit.cs test/Capacitor.Cli.Tests.Unit/Services/LaunchdUnitTests.cs
git commit -m "feat(daemon): launchd plist + command vectors (pure)"
```

---

## Task 3: systemd pure unit (`SystemdUnit`)

**Files:**
- Create: `src/Capacitor.Cli/Services/SystemdUnit.cs`
- Test: `test/Capacitor.Cli.Tests.Unit/Services/SystemdUnitTests.cs`

- [ ] **Step 1: Write the failing test**

`test/Capacitor.Cli.Tests.Unit/Services/SystemdUnitTests.cs`:
```csharp
using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Services;

public class SystemdUnitTests {
    static ServiceSpec Spec(string id = "laptop") => new(
        id, "/opt/kcap/kcap-daemon", "/home/u/.config/kcap/daemon-laptop.log",
        new Dictionary<string, string> { ["PATH"] = "/usr/bin", ["KCAP_PROFILE"] = "work" },
        ["--max-agents", "8"]);

    [Test]
    public async Task UnitName_is_per_instance() {
        await Assert.That(SystemdUnit.UnitName("laptop")).IsEqualTo("kcap-daemon-laptop.service");
    }

    [Test]
    public async Task Unit_has_execstart_restart_and_env() {
        var unit = SystemdUnit.Unit(Spec());
        await Assert.That(unit).Contains("ExecStart=/opt/kcap/kcap-daemon --name laptop --log-file /home/u/.config/kcap/daemon-laptop.log --max-agents 8");
        await Assert.That(unit).Contains("Restart=on-failure");
        await Assert.That(unit).Contains("Environment=PATH=/usr/bin");
        await Assert.That(unit).Contains("Environment=KCAP_PROFILE=work");
        await Assert.That(unit).Contains("WantedBy=default.target");
    }

    [Test]
    public async Task IdFromUnitFileName_extracts_id() {
        await Assert.That(SystemdUnit.IdFromUnitFileName("kcap-daemon-laptop.service")).IsEqualTo("laptop");
        await Assert.That(SystemdUnit.IdFromUnitFileName("other.service")).IsNull();
    }

    [Test]
    public async Task StatusFrom_maps_active_strings() {
        await Assert.That(SystemdUnit.StatusFrom(activeOut: "active", enabledExit: 0)).IsEqualTo(ServiceState.Running);
        await Assert.That(SystemdUnit.StatusFrom("inactive", 0)).IsEqualTo(ServiceState.Installed);
        await Assert.That(SystemdUnit.StatusFrom("inactive", 1)).IsEqualTo(ServiceState.NotInstalled);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/SystemdUnitTests/*"`
Expected: FAIL (compile error — `SystemdUnit` does not exist).

- [ ] **Step 3: Write `SystemdUnit`**

`src/Capacitor.Cli/Services/SystemdUnit.cs`:
```csharp
using System.Text;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Services;

/// <summary>Pure rendering + command vectors for a per-user systemd unit (one file per id).</summary>
static class SystemdUnit {
    const string Prefix = "kcap-daemon-";

    public static string UnitName(string id) => $"{Prefix}{id}.service";

    public static string UserUnitDir() =>
        Path.Combine(PathHelpers.HomeDirectory, ".config", "systemd", "user");

    public static string UnitPath(string id) => Path.Combine(UserUnitDir(), UnitName(id));

    public static string Unit(ServiceSpec spec) {
        var sb = new StringBuilder();
        sb.Append("[Unit]\n");
        sb.Append($"Description=kcap daemon ({ServiceText.SystemdValue(spec.ServiceId)})\n");
        sb.Append("After=network-online.target\n");
        sb.Append("Wants=network-online.target\n\n");

        sb.Append("[Service]\n");
        foreach (var (k, v) in spec.Environment)
            sb.Append($"Environment={ServiceText.SystemdValue(k)}={ServiceText.SystemdValue(v)}\n");

        var args = string.Join(' ', new[] { "--name", spec.ServiceId, "--log-file", spec.LogPath }.Concat(spec.ExtraArgs));
        sb.Append($"ExecStart={spec.DaemonBinaryPath} {args}\n");
        sb.Append("Restart=on-failure\n");
        sb.Append("RestartSec=5\n");
        sb.Append("StartLimitIntervalSec=60\n");
        sb.Append("StartLimitBurst=5\n\n");

        sb.Append("[Install]\n");
        sb.Append("WantedBy=default.target\n");
        return sb.ToString();
    }

    public static string? IdFromUnitFileName(string fileName) =>
        fileName.StartsWith(Prefix, StringComparison.Ordinal) && fileName.EndsWith(".service", StringComparison.Ordinal)
            ? fileName[Prefix.Length..^".service".Length]
            : null;

    // ── command vectors ──
    public static string[] DaemonReloadArgs()      => ["--user", "daemon-reload"];
    public static string[] EnableArgs(string id)   => ["--user", "enable", UnitName(id)];
    public static string[] DisableNowArgs(string id)=> ["--user", "disable", "--now", UnitName(id)];
    public static string[] StartArgs(string id)    => ["--user", "start", UnitName(id)];
    public static string[] RestartArgs(string id)  => ["--user", "restart", UnitName(id)];
    public static string[] StopArgs(string id)     => ["--user", "stop", UnitName(id)];
    public static string[] IsActiveArgs(string id) => ["--user", "is-active", UnitName(id)];
    public static string[] IsEnabledArgs(string id)=> ["--user", "is-enabled", UnitName(id)];

    public static ServiceState StatusFrom(string activeOut, int enabledExit) {
        if (activeOut.Trim().Equals("active", StringComparison.OrdinalIgnoreCase)) return ServiceState.Running;
        return enabledExit == 0 ? ServiceState.Installed : ServiceState.NotInstalled;
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/SystemdUnitTests/*"`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli/Services/SystemdUnit.cs test/Capacitor.Cli.Tests.Unit/Services/SystemdUnitTests.cs
git commit -m "feat(daemon): systemd per-instance unit + vectors (pure)"
```

---

## Task 4: Windows pure unit (`WindowsTaskUnit`)

**Files:**
- Create: `src/Capacitor.Cli/Services/WindowsTaskUnit.cs`
- Test: `test/Capacitor.Cli.Tests.Unit/Services/WindowsTaskUnitTests.cs`

- [ ] **Step 1: Write the failing test**

`test/Capacitor.Cli.Tests.Unit/Services/WindowsTaskUnitTests.cs`:
```csharp
using System.Xml.Linq;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Services;

public class WindowsTaskUnitTests {
    static ServiceSpec Spec(string id = "laptop") => new(
        id, @"C:\kcap\kcap-daemon.exe", @"C:\Users\u\.config\kcap\daemon-laptop.log",
        new Dictionary<string, string> { ["PATH"] = @"C:\bin", ["KCAP_PROFILE"] = "work" },
        ["--max-agents", "8"]);

    [Test]
    public async Task TaskName_is_per_id() {
        await Assert.That(WindowsTaskUnit.TaskName("laptop")).IsEqualTo("kcap-daemon-laptop");
    }

    [Test]
    public async Task Wrapper_sets_env_and_execs_daemon() {
        var cmd = WindowsTaskUnit.Wrapper(Spec());
        await Assert.That(cmd).Contains("set \"PATH=C:\\bin\"");
        await Assert.That(cmd).Contains("set \"KCAP_PROFILE=work\"");
        await Assert.That(cmd).Contains("\"C:\\kcap\\kcap-daemon.exe\" --name laptop --log-file \"C:\\Users\\u\\.config\\kcap\\daemon-laptop.log\" --max-agents 8");
    }

    [Test]
    public async Task Wrapper_doubles_percent_in_values() {
        var spec = Spec() with { Environment = new Dictionary<string, string> { ["X"] = "50%done" } };
        await Assert.That(WindowsTaskUnit.Wrapper(spec)).Contains("set \"X=50%%done\"");
    }

    [Test]
    public async Task TaskXml_is_well_formed_and_runs_cmd_wrapper() {
        var xml = WindowsTaskUnit.TaskXml(Spec(), @"C:\Users\u\.config\kcap\daemon-service-laptop.cmd");
        XDocument.Parse(xml); // throws if malformed
        await Assert.That(xml).Contains("<Command>cmd.exe</Command>");
        await Assert.That(xml).Contains("/c");
        await Assert.That(xml).Contains("daemon-service-laptop.cmd");
        await Assert.That(xml).Contains("<LogonTrigger>");
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/WindowsTaskUnitTests/*"`
Expected: FAIL (compile error — `WindowsTaskUnit` does not exist).

- [ ] **Step 3: Write `WindowsTaskUnit`**

`src/Capacitor.Cli/Services/WindowsTaskUnit.cs`:
```csharp
using System.Text;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Services;

/// <summary>Pure rendering + command vectors for a per-user Windows logon Scheduled Task.</summary>
static class WindowsTaskUnit {
    const string Prefix = "kcap-daemon-";

    public static string TaskName(string id) => Prefix + id;

    public static string WrapperPath(string id) => PathHelpers.ConfigPath($"daemon-service-{id}.cmd");

    /// <summary>.cmd wrapper: set the captured env, then exec the daemon (no Environment element in Task XML).</summary>
    public static string Wrapper(ServiceSpec spec) {
        var sb = new StringBuilder();
        sb.Append("@echo off\r\n");
        foreach (var (k, v) in spec.Environment)
            sb.Append($"set \"{ServiceText.CmdValue(k)}={ServiceText.CmdValue(v)}\"\r\n");
        var args = string.Join(' ',
            new[] { "--name", spec.ServiceId, "--log-file", Quote(spec.LogPath) }
                .Concat(spec.ExtraArgs.Select(QuoteIfNeeded)));
        sb.Append($"{Quote(ServiceText.CmdValue(spec.DaemonBinaryPath))} {args}\r\n");
        return sb.ToString();
    }

    static string Quote(string s) => $"\"{s}\"";
    static string QuoteIfNeeded(string s) => s.Contains(' ') ? Quote(s) : s;

    public static string TaskXml(ServiceSpec spec, string wrapperPath) =>
        $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo>
            <Description>kcap daemon ({ServiceText.Xml(spec.ServiceId)})</Description>
          </RegistrationInfo>
          <Triggers>
            <LogonTrigger><Enabled>true</Enabled></LogonTrigger>
          </Triggers>
          <Settings>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
            <StartWhenAvailable>true</StartWhenAvailable>
            <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
            <RestartOnFailure><Interval>PT1M</Interval><Count>999</Count></RestartOnFailure>
          </Settings>
          <Actions>
            <Exec>
              <Command>cmd.exe</Command>
              <Arguments>/c "{ServiceText.Xml(wrapperPath)}"</Arguments>
            </Exec>
          </Actions>
        </Task>
        """;

    public static string? IdFromTaskName(string taskName) =>
        taskName.StartsWith(Prefix, StringComparison.Ordinal) ? taskName[Prefix.Length..] : null;

    // ── command vectors (schtasks) ──
    public static string[] CreateArgs(string id, string xmlPath) => ["/Create", "/TN", TaskName(id), "/XML", xmlPath, "/F"];
    public static string[] DeleteArgs(string id)                 => ["/Delete", "/TN", TaskName(id), "/F"];
    public static string[] RunArgs(string id)                    => ["/Run", "/TN", TaskName(id)];
    public static string[] EndArgs(string id)                    => ["/End", "/TN", TaskName(id)];
    public static string[] QueryArgs(string id)                  => ["/Query", "/TN", TaskName(id), "/FO", "LIST"];

    public static ServiceState StatusFromQuery(int exitCode, string stdout) {
        if (exitCode != 0) return ServiceState.NotInstalled;
        return stdout.Contains("Running", StringComparison.OrdinalIgnoreCase)
            ? ServiceState.Running
            : ServiceState.Installed;
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/WindowsTaskUnitTests/*"`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli/Services/WindowsTaskUnit.cs test/Capacitor.Cli.Tests.Unit/Services/WindowsTaskUnitTests.cs
git commit -m "feat(daemon): windows task xml + cmd wrapper + vectors (pure)"
```

---

## Task 5: Process runner, thin managers, and factory

**Files:**
- Create: `src/Capacitor.Cli/Services/ServiceProcess.cs`
- Create: `src/Capacitor.Cli/Services/LaunchdServiceManager.cs`
- Create: `src/Capacitor.Cli/Services/SystemdServiceManager.cs`
- Create: `src/Capacitor.Cli/Services/WindowsScheduledTaskServiceManager.cs`
- Create: `src/Capacitor.Cli/Services/ServiceManagerFactory.cs`
- Test: `test/Capacitor.Cli.Tests.Unit/Services/ServiceManagerFactoryTests.cs`

- [ ] **Step 1: Write the failing factory test**

`test/Capacitor.Cli.Tests.Unit/Services/ServiceManagerFactoryTests.cs`:
```csharp
using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Services;

public class ServiceManagerFactoryTests {
    [Test]
    public async Task ForPlatform_returns_each_concrete_manager() {
        await Assert.That(ServiceManagerFactory.ForPlatform(ServicePlatform.Launchd)).IsTypeOf<LaunchdServiceManager>();
        await Assert.That(ServiceManagerFactory.ForPlatform(ServicePlatform.Systemd)).IsTypeOf<SystemdServiceManager>();
        await Assert.That(ServiceManagerFactory.ForPlatform(ServicePlatform.WindowsScheduledTask)).IsTypeOf<WindowsScheduledTaskServiceManager>();
    }

    [Test]
    public async Task ForCurrentOs_does_not_throw_on_this_host() {
        var mgr = ServiceManagerFactory.ForCurrentOs();
        await Assert.That(mgr.Describe()).IsNotNull();
    }

    [Test]
    public async Task Launchd_GenerateFiles_returns_one_file() {
        var spec = new ServiceSpec("laptop", "/opt/kcap/kcap-daemon", "/tmp/daemon-laptop.log",
            new Dictionary<string, string>(), []);
        var files = ServiceManagerFactory.ForPlatform(ServicePlatform.Launchd).GenerateFiles(spec);
        await Assert.That(files.Count).IsEqualTo(1);
        await Assert.That(files[0].Path).EndsWith("io.kurrent.kcap.daemon.laptop.plist");
    }

    [Test]
    public async Task Windows_GenerateFiles_returns_xml_and_wrapper() {
        var spec = new ServiceSpec("laptop", @"C:\kcap\kcap-daemon.exe", @"C:\tmp\daemon-laptop.log",
            new Dictionary<string, string>(), []);
        var files = ServiceManagerFactory.ForPlatform(ServicePlatform.WindowsScheduledTask).GenerateFiles(spec);
        await Assert.That(files.Count).IsEqualTo(2);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/ServiceManagerFactoryTests/*"`
Expected: FAIL (compile error — managers/factory don't exist).

- [ ] **Step 3: Write the process runner**

`src/Capacitor.Cli/Services/ServiceProcess.cs`:
```csharp
using System.Diagnostics;

namespace Capacitor.Cli.Services;

/// <summary>
/// Minimal synchronous shell-out for service registration tools
/// (launchctl/systemctl/schtasks). Not used in tests — managers' side-effecting
/// methods are the one part not exercised in CI.
/// </summary>
static class ServiceProcess {
    public static (int ExitCode, string StdOut, string StdErr) Run(string file, params string[] args) {
        var psi = new ProcessStartInfo {
            FileName               = file,
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {file}");
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, stdout, stderr);
    }

    /// <summary>Run and throw with captured stderr on non-zero exit.</summary>
    public static void Check(string file, params string[] args) {
        var (code, _, err) = Run(file, args);
        if (code != 0)
            throw new InvalidOperationException($"{file} {string.Join(' ', args)} failed (exit {code}): {err.Trim()}");
    }
}
```

- [ ] **Step 4: Write the launchd manager**

`src/Capacitor.Cli/Services/LaunchdServiceManager.cs`:
```csharp
using System.Runtime.InteropServices;

namespace Capacitor.Cli.Services;

sealed partial class LaunchdServiceManager : IServiceManager {
    [LibraryImport("libc", EntryPoint = "getuid")]
    private static partial uint getuid();

    static int Uid() => (int)getuid();

    public string Describe() => "launchd LaunchAgent";

    public IReadOnlyList<GeneratedFile> GenerateFiles(ServiceSpec spec) =>
        [new GeneratedFile(LaunchdUnit.PlistPath(spec.ServiceId), LaunchdUnit.Plist(spec))];

    public IReadOnlyList<string> ListInstalled() {
        var dir = LaunchdUnit.AgentsDir();
        if (!Directory.Exists(dir)) return [];
        return [.. Directory.EnumerateFiles(dir, "io.kurrent.kcap.daemon.*.plist")
            .Select(f => LaunchdUnit.IdFromPlistFileName(Path.GetFileName(f)))
            .Where(id => id is not null).Select(id => id!).Order()];
    }

    public ServiceStatus Status(string serviceId) {
        var path = LaunchdUnit.PlistPath(serviceId);
        if (!File.Exists(path)) return new ServiceStatus(ServiceState.NotInstalled, null);
        // First <string> in the plist is the daemon binary (ProgramArguments[0]) — for doctor.
        var bin = System.Xml.Linq.XDocument.Load(path).Descendants("string").FirstOrDefault()?.Value;
        var (code, stdout, _) = ServiceProcess.Run("launchctl", LaunchdUnit.PrintArgs(Uid(), serviceId));
        return new ServiceStatus(LaunchdUnit.StatusFromPrint(code, stdout), bin);
    }

    public void Install(ServiceSpec spec, bool startNow) {
        Directory.CreateDirectory(LaunchdUnit.AgentsDir());
        var plistPath = LaunchdUnit.PlistPath(spec.ServiceId);
        // idempotent: bootout an existing job (ignore failure), then rewrite + bootstrap.
        ServiceProcess.Run("launchctl", LaunchdUnit.BootoutArgs(Uid(), spec.ServiceId));
        File.WriteAllText(plistPath, LaunchdUnit.Plist(spec));
        ServiceProcess.Check("launchctl", LaunchdUnit.BootstrapArgs(Uid(), plistPath)); // RunAtLoad starts it
        if (!startNow) ServiceProcess.Run("launchctl", LaunchdUnit.KillArgs(Uid(), spec.ServiceId));
    }

    public void Uninstall(string serviceId) {
        ServiceProcess.Run("launchctl", LaunchdUnit.BootoutArgs(Uid(), serviceId));
        var path = LaunchdUnit.PlistPath(serviceId);
        if (File.Exists(path)) File.Delete(path);
    }

    public void Start(string serviceId) => ServiceProcess.Check("launchctl", LaunchdUnit.KickstartArgs(Uid(), serviceId));
    public void Stop(string serviceId)  => ServiceProcess.Check("launchctl", LaunchdUnit.KillArgs(Uid(), serviceId));
}
```

- [ ] **Step 5: Write the systemd manager**

`src/Capacitor.Cli/Services/SystemdServiceManager.cs`:
```csharp
namespace Capacitor.Cli.Services;

sealed class SystemdServiceManager : IServiceManager {
    public string Describe() => "systemd --user unit";

    public IReadOnlyList<GeneratedFile> GenerateFiles(ServiceSpec spec) =>
        [new GeneratedFile(SystemdUnit.UnitPath(spec.ServiceId), SystemdUnit.Unit(spec))];

    public IReadOnlyList<string> ListInstalled() {
        var dir = SystemdUnit.UserUnitDir();
        if (!Directory.Exists(dir)) return [];
        return [.. Directory.EnumerateFiles(dir, "kcap-daemon-*.service")
            .Select(f => SystemdUnit.IdFromUnitFileName(Path.GetFileName(f)))
            .Where(id => id is not null).Select(id => id!).Order()];
    }

    public ServiceStatus Status(string serviceId) {
        var path = SystemdUnit.UnitPath(serviceId);
        if (!File.Exists(path)) return new ServiceStatus(ServiceState.NotInstalled, null);
        var (_, active, _)      = ServiceProcess.Run("systemctl", SystemdUnit.IsActiveArgs(serviceId));
        var (enabledExit, _, _) = ServiceProcess.Run("systemctl", SystemdUnit.IsEnabledArgs(serviceId));
        var bin = ExecStartBinary(path);
        return new ServiceStatus(SystemdUnit.StatusFrom(active, enabledExit), bin);
    }

    static string? ExecStartBinary(string unitPath) {
        var line = File.ReadLines(unitPath).FirstOrDefault(l => l.StartsWith("ExecStart=", StringComparison.Ordinal));
        return line?["ExecStart=".Length..].Split(' ', 2)[0];
    }

    public void Install(ServiceSpec spec, bool startNow) {
        Directory.CreateDirectory(SystemdUnit.UserUnitDir());
        File.WriteAllText(SystemdUnit.UnitPath(spec.ServiceId), SystemdUnit.Unit(spec));
        ServiceProcess.Check("systemctl", SystemdUnit.DaemonReloadArgs());
        ServiceProcess.Check("systemctl", SystemdUnit.EnableArgs(spec.ServiceId));
        if (startNow) ServiceProcess.Check("systemctl", SystemdUnit.RestartArgs(spec.ServiceId));
    }

    public void Uninstall(string serviceId) {
        ServiceProcess.Run("systemctl", SystemdUnit.DisableNowArgs(serviceId));
        var path = SystemdUnit.UnitPath(serviceId);
        if (File.Exists(path)) File.Delete(path);
        ServiceProcess.Run("systemctl", SystemdUnit.DaemonReloadArgs());
    }

    public void Start(string serviceId) => ServiceProcess.Check("systemctl", SystemdUnit.StartArgs(serviceId));
    public void Stop(string serviceId)  => ServiceProcess.Check("systemctl", SystemdUnit.StopArgs(serviceId));
}
```

- [ ] **Step 6: Write the Windows manager**

`src/Capacitor.Cli/Services/WindowsScheduledTaskServiceManager.cs`:
```csharp
namespace Capacitor.Cli.Services;

sealed class WindowsScheduledTaskServiceManager : IServiceManager {
    public string Describe() => "Windows Scheduled Task";

    public IReadOnlyList<GeneratedFile> GenerateFiles(ServiceSpec spec) {
        var wrapperPath = WindowsTaskUnit.WrapperPath(spec.ServiceId);
        return [
            new GeneratedFile(wrapperPath, WindowsTaskUnit.Wrapper(spec)),
            new GeneratedFile(TaskXmlTempPath(spec.ServiceId), WindowsTaskUnit.TaskXml(spec, wrapperPath)),
        ];
    }

    static string TaskXmlTempPath(string id) =>
        Capacitor.Cli.Core.PathHelpers.ConfigPath($"daemon-service-{id}.task.xml");

    public IReadOnlyList<string> ListInstalled() {
        var (code, stdout, _) = ServiceProcess.Run("schtasks", "/Query", "/FO", "LIST");
        if (code != 0) return [];
        return [.. stdout.Split('\n')
            .Where(l => l.TrimStart().StartsWith("TaskName:", StringComparison.OrdinalIgnoreCase))
            .Select(l => WindowsTaskUnit.IdFromTaskName(Path.GetFileName(l.Split(':', 2)[1].Trim())))
            .Where(id => id is not null).Select(id => id!).Distinct().Order()];
    }

    public ServiceStatus Status(string serviceId) {
        var (code, stdout, _) = ServiceProcess.Run("schtasks", WindowsTaskUnit.QueryArgs(serviceId));
        var bin = File.Exists(WindowsTaskUnit.WrapperPath(serviceId)) ? WindowsTaskUnit.WrapperPath(serviceId) : null;
        return new ServiceStatus(WindowsTaskUnit.StatusFromQuery(code, stdout), bin);
    }

    public void Install(ServiceSpec spec, bool startNow) {
        var files = GenerateFiles(spec);
        foreach (var f in files) { Directory.CreateDirectory(Path.GetDirectoryName(f.Path)!); File.WriteAllText(f.Path, f.Content); }
        var xmlPath = files.First(f => f.Path.EndsWith(".task.xml", StringComparison.Ordinal)).Path;
        ServiceProcess.Check("schtasks", WindowsTaskUnit.CreateArgs(spec.ServiceId, xmlPath));
        File.Delete(xmlPath); // the task XML is only needed for registration
        if (startNow) ServiceProcess.Check("schtasks", WindowsTaskUnit.RunArgs(spec.ServiceId));
    }

    public void Uninstall(string serviceId) {
        ServiceProcess.Run("schtasks", WindowsTaskUnit.DeleteArgs(serviceId));
        var wrapper = WindowsTaskUnit.WrapperPath(serviceId);
        if (File.Exists(wrapper)) File.Delete(wrapper);
    }

    public void Start(string serviceId) => ServiceProcess.Check("schtasks", WindowsTaskUnit.RunArgs(serviceId));
    public void Stop(string serviceId)  => ServiceProcess.Check("schtasks", WindowsTaskUnit.EndArgs(serviceId));
}
```

- [ ] **Step 7: Write the factory**

`src/Capacitor.Cli/Services/ServiceManagerFactory.cs`:
```csharp
namespace Capacitor.Cli.Services;

static class ServiceManagerFactory {
    public static IServiceManager ForPlatform(ServicePlatform platform) => platform switch {
        ServicePlatform.Launchd              => new LaunchdServiceManager(),
        ServicePlatform.Systemd              => new SystemdServiceManager(),
        ServicePlatform.WindowsScheduledTask => new WindowsScheduledTaskServiceManager(),
        _ => throw new PlatformNotSupportedException($"No service manager for {platform}"),
    };

    public static IServiceManager ForCurrentOs() {
        if (OperatingSystem.IsMacOS())   return ForPlatform(ServicePlatform.Launchd);
        if (OperatingSystem.IsLinux())   return ForPlatform(ServicePlatform.Systemd);
        if (OperatingSystem.IsWindows()) return ForPlatform(ServicePlatform.WindowsScheduledTask);
        throw new PlatformNotSupportedException("kcap daemon service is not supported on this OS.");
    }
}
```

- [ ] **Step 8: Run the factory test to verify it passes**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/ServiceManagerFactoryTests/*"`
Expected: PASS (4 tests).

- [ ] **Step 9: Commit**

```bash
git add src/Capacitor.Cli/Services/ServiceProcess.cs src/Capacitor.Cli/Services/LaunchdServiceManager.cs src/Capacitor.Cli/Services/SystemdServiceManager.cs src/Capacitor.Cli/Services/WindowsScheduledTaskServiceManager.cs src/Capacitor.Cli/Services/ServiceManagerFactory.cs test/Capacitor.Cli.Tests.Unit/Services/ServiceManagerFactoryTests.cs
git commit -m "feat(daemon): thin service managers + process runner + factory"
```

---

## Task 6: Environment capture + pinned profile (`ServiceEnvironment`)

**Files:**
- Create: `src/Capacitor.Cli/Services/ServiceEnvironment.cs`
- Test: `test/Capacitor.Cli.Tests.Unit/Services/ServiceEnvironmentTests.cs`

- [ ] **Step 1: Write the failing test**

`test/Capacitor.Cli.Tests.Unit/Services/ServiceEnvironmentTests.cs`:
```csharp
using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Services;

public class ServiceEnvironmentTests {
    [Test]
    public async Task Build_pins_profile_and_includes_path() {
        var src = new Dictionary<string, string> {
            ["PATH"]              = "/usr/local/bin:/usr/bin",
            ["KCAP_CONFIG_DIR"]   = "/home/u/.config/kcap",
            ["IRRELEVANT"]        = "x",
        };
        var env = ServiceEnvironment.Build(profileName: "work", source: src);
        await Assert.That(env["PATH"]).IsEqualTo("/usr/local/bin:/usr/bin");
        await Assert.That(env["KCAP_PROFILE"]).IsEqualTo("work");
        await Assert.That(env["KCAP_CONFIG_DIR"]).IsEqualTo("/home/u/.config/kcap");
        await Assert.That(env.ContainsKey("IRRELEVANT")).IsFalse();
    }

    [Test]
    public async Task Build_omits_profile_when_null_and_keeps_kcap_url() {
        var src = new Dictionary<string, string> { ["KCAP_URL"] = "https://x" };
        var env = ServiceEnvironment.Build(profileName: null, source: src);
        await Assert.That(env.ContainsKey("KCAP_PROFILE")).IsFalse();
        await Assert.That(env["KCAP_URL"]).IsEqualTo("https://x");
    }

    [Test]
    public async Task Build_explicit_profile_overrides_source_env() {
        var src = new Dictionary<string, string> { ["KCAP_PROFILE"] = "old" };
        var env = ServiceEnvironment.Build(profileName: "new", source: src);
        await Assert.That(env["KCAP_PROFILE"]).IsEqualTo("new");
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/ServiceEnvironmentTests/*"`
Expected: FAIL (compile error — `ServiceEnvironment` does not exist).

- [ ] **Step 3: Write `ServiceEnvironment`**

`src/Capacitor.Cli/Services/ServiceEnvironment.cs`:
```csharp
using System.Collections;

namespace Capacitor.Cli.Services;

/// <summary>
/// Builds the environment baked into a service unit. Supervised jobs don't
/// inherit the interactive shell PATH (so bare claude/codex lookup fails) and a
/// baked --server-url would null out profile resolution — so we capture PATH +
/// the KCAP_* keys and pin the profile via KCAP_PROFILE.
/// </summary>
static class ServiceEnvironment {
    static readonly string[] Keys = ["PATH", "KCAP_CONFIG_DIR", "KCAP_PROFILE", "KCAP_URL", "KCAP_CLAUDE_PATH", "KCAP_CODEX_PATH"];

    /// <summary>Production entry point: capture from the current process env.</summary>
    public static IReadOnlyDictionary<string, string> Capture(string? profileName) =>
        Build(profileName, Snapshot());

    static Dictionary<string, string> Snapshot() {
        var d = new Dictionary<string, string>();
        foreach (DictionaryEntry e in Environment.GetEnvironmentVariables())
            if (e.Key is string k && e.Value is string v) d[k] = v;
        return d;
    }

    /// <summary>Pure: select the relevant keys from <paramref name="source"/>, pin the profile.</summary>
    public static IReadOnlyDictionary<string, string> Build(string? profileName, IReadOnlyDictionary<string, string> source) {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in Keys)
            if (source.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v)) env[key] = v;
        if (!string.IsNullOrEmpty(profileName)) env["KCAP_PROFILE"] = profileName; // explicit pin wins
        return env;
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/ServiceEnvironmentTests/*"`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli/Services/ServiceEnvironment.cs test/Capacitor.Cli.Tests.Unit/Services/ServiceEnvironmentTests.cs
git commit -m "feat(daemon): capture env + pin profile for service units"
```

---

## Task 7: `kcap daemon service` subcommand dispatch

**Files:**
- Modify: `src/Capacitor.Cli/Commands/DaemonCommands.cs` (add `service` case + handler)
- Test: manual smoke (the dispatch is thin glue; pure logic is already covered)

- [ ] **Step 1: Add the `service` dispatch arm**

In `DaemonCommands.HandleAsync`, the `subcommand switch` (around `DaemonCommands.cs:20-27`), add an arm:
```csharp
        return subcommand switch {
            "start"   => await StartAsync(remaining),
            "stop"    => await StopAsync(remaining),
            "status"  => await Status(remaining),
            "logs"    => await Logs(),
            "doctor"  => await DoctorAsync(remaining),
            "service" => await ServiceAsync(remaining),
            _         => PrintUsage()
        };
```

- [ ] **Step 2: Add the `ServiceAsync` handler**

Add to `DaemonCommands` (new region near the bottom, before `ResolveDaemonBinary`):
```csharp
    // ── service (OS supervisor: launchd / systemd / scheduled task) ───────────

    static async Task<int> ServiceAsync(string[] args) {
        if (args.Length == 0) return ServiceUsage();

        var action    = args[0];
        var rest      = args[1..];
        var noStart   = rest.Contains("--no-start");

        IServiceManager manager;
        try {
            manager = ServiceManagerFactory.ForCurrentOs();
        } catch (PlatformNotSupportedException ex) {
            await Console.Error.WriteLineAsync(ex.Message);
            return 1;
        }

        var id = ServiceText.ServiceId(ResolveName(rest));

        switch (action) {
            case "install":   return await ServiceInstall(manager, rest, id, startNow: !noStart);
            case "uninstall": manager.Uninstall(id); await Console.Out.WriteLineAsync($"Service '{id}' uninstalled ({manager.Describe()})."); return 0;
            case "start":     manager.Start(id);     await Console.Out.WriteLineAsync($"Service '{id}' started.");   return 0;
            case "stop":      manager.Stop(id);      await Console.Out.WriteLineAsync($"Service '{id}' stopped (still installed)."); return 0;
            case "status":    return await ServiceStatus(manager, id);
            default:          return ServiceUsage();
        }
    }

    static async Task<int> ServiceInstall(IServiceManager manager, string[] args, string id, bool startNow) {
        var daemonPath = ResolveDaemonBinary();
        if (daemonPath is null) { await Console.Error.WriteLineAsync(DaemonNotFoundMessage()); return 1; }

        var profileName = ExtractFlagValue(args, "--profile") ?? AppConfig.ResolvedProfile?.ProfileName;
        var env         = ServiceEnvironment.Capture(profileName);

        var extra = new List<string>();
        if (ExtractFlagValue(args, "--max-agents") is { } mx) { extra.Add("--max-agents"); extra.Add(mx); }

        var logPath = PathHelpers.ConfigPath($"daemon-{id}.log");
        var spec    = new ServiceSpec(id, daemonPath, logPath, env, extra);

        manager.Install(spec, startNow);

        await Console.Out.WriteLineAsync($"Service '{id}' installed ({manager.Describe()}).");
        await Console.Out.WriteLineAsync($"  Auto-restarts on crash/SIGKILL; starts at login.");
        await Console.Out.WriteLineAsync($"  Log:       {logPath}");
        await Console.Out.WriteLineAsync($"  Stop:      kcap daemon service stop --name {id}");
        await Console.Out.WriteLineAsync($"  Remove:    kcap daemon service uninstall --name {id}");
        return 0;
    }

    static async Task<int> ServiceStatus(IServiceManager manager, string id) {
        var status = manager.Status(id);
        await Console.Out.WriteLineAsync($"Service '{id}': {status.State} ({manager.Describe()})");
        if (status.BinaryPath is { } bin) await Console.Out.WriteLineAsync($"  binary: {bin}");
        return 0;
    }

    static int ServiceUsage() {
        Console.Error.WriteLine("Usage: kcap daemon service <install|uninstall|start|stop|status> [--name N]");
        Console.Error.WriteLine();
        Console.Error.WriteLine("  install [--name N] [--profile P] [--max-agents N] [--no-start]");
        Console.Error.WriteLine("  uninstall [--name N]   Stop and remove the service unit");
        Console.Error.WriteLine("  start [--name N]       Start the installed service now");
        Console.Error.WriteLine("  stop [--name N]        Stop the running service (stays installed)");
        Console.Error.WriteLine("  status [--name N]      Show installed/running state");
        return 1;
    }
```

- [ ] **Step 3: Add the using directive**

At the top of `DaemonCommands.cs`, add:
```csharp
using Capacitor.Cli.Services;
```

- [ ] **Step 4: Build and smoke-test usage**

Run: `dotnet build src/Capacitor.Cli/Capacitor.Cli.csproj`
Expected: build succeeds.

Run: `dotnet run --project src/Capacitor.Cli/Capacitor.Cli.csproj -- daemon service`
Expected: prints the service usage block, exit 1.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli/Commands/DaemonCommands.cs
git commit -m "feat(daemon): kcap daemon service install/uninstall/start/stop/status"
```

---

## Task 8: status / stop / doctor service-awareness

**Files:**
- Modify: `src/Capacitor.Cli/Commands/DaemonCommands.cs`

- [ ] **Step 1: `status` shows installed-but-stopped services**

In `Status` (`DaemonCommands.cs:308`), after computing `names` from PID files, union with installed service ids and print a `service:` line. Replace the body that builds/uses `names` with:
```csharp
        var manager = TryServiceManager();
        var serviceIds = manager?.ListInstalled() ?? [];
        var names = explicitName is not null
            ? new List<string> { explicitName }
            : EnumerateRunningNames().Concat(serviceIds.Select(id => id)).Distinct().Order().ToList();

        if (names.Count == 0) {
            await Console.Out.WriteLineAsync("Daemon: not running");
            return 0;
        }

        foreach (var name in names) {
            var id = ServiceText.ServiceId(name);
            // existing running/stale PID reporting:
            if (ReadPidFile(name) is { } entry && IsOurDaemon(entry.Pid, entry.StartTicks))
                await Console.Out.WriteLineAsync($"Daemon '{name}': running (PID {entry.Pid})");
            else
                await Console.Out.WriteLineAsync($"Daemon '{name}': not running");

            if (manager is not null) {
                var st = manager.Status(id).State;
                if (st != ServiceState.NotInstalled)
                    await Console.Out.WriteLineAsync($"  service: {st} ({manager.Describe()})");
            }
        }
        return 0;
```

Add helper near `ResolveDaemonBinary`:
```csharp
    static IServiceManager? TryServiceManager() {
        try { return ServiceManagerFactory.ForCurrentOs(); }
        catch (PlatformNotSupportedException) { return null; }
    }
```

- [ ] **Step 2: `stop` defers to the supervisor for service-managed ids**

In `StopByName` (`DaemonCommands.cs:277`), at the top, add:
```csharp
        var manager = TryServiceManager();
        if (manager is not null && manager.Status(ServiceText.ServiceId(name)).State != ServiceState.NotInstalled) {
            Console.Out.WriteLine(
                $"Daemon '{name}' is managed by {manager.Describe()}; a raw stop would be auto-restarted.");
            Console.Out.WriteLine($"Use: kcap daemon service stop --name {name}  (or uninstall to remove it)");
            return 0;
        }
```

- [ ] **Step 3: `doctor` validates installed-service binary paths**

At the end of `DoctorAsync` (before `return 0;` at `DaemonCommands.cs:464`), add:
```csharp
        var svcManager = TryServiceManager();
        if (svcManager is not null) {
            var installed = svcManager.ListInstalled();
            if (installed.Count > 0) {
                await Console.Out.WriteLineAsync($"\nInstalled services ({svcManager.Describe()}):");
                foreach (var sid in installed) {
                    var st  = svcManager.Status(sid);
                    var bad = st.BinaryPath is { } b && !File.Exists(b);
                    var note = bad ? "  ⚠ binary missing — re-run `kcap daemon service install`" : "";
                    await Console.Out.WriteLineAsync($"  {sid,-20}  {st.State}{note}");
                }
            }
        }
```

- [ ] **Step 4: Build and verify**

Run: `dotnet build src/Capacitor.Cli/Capacitor.Cli.csproj`
Expected: build succeeds.

Run: `dotnet run --project src/Capacitor.Cli/Capacitor.Cli.csproj -- daemon status`
Expected: prints daemon status; no crash when no services installed.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli/Commands/DaemonCommands.cs
git commit -m "feat(daemon): status/stop/doctor awareness of installed services"
```

---

## Task 9: Docs (help-daemon.txt + README)

**Files:**
- Modify: `src/Capacitor.Cli.Core/Resources/help-daemon.txt`
- Modify: `README.md`

- [ ] **Step 1: Add the `service` group to `help-daemon.txt`**

After the `doctor` line in the Subcommands block (`help-daemon.txt:10`), add:
```
  service <action>       Manage the OS service (launchd/systemd/Scheduled Task)
```
And after the `doctor` options block, append:
```

Subcommands for service (per-user; auto-restarts on crash/SIGKILL, starts at login):
  install [--name N] [--profile P] [--max-agents N] [--no-start]
                          Register the daemon as an OS service and start it.
                          Pins the profile via KCAP_PROFILE and captures PATH so
                          claude/codex resolve as they do in your shell.
  uninstall [--name N]    Stop and remove the service unit.
  start [--name N]        Start the installed service now.
  stop [--name N]         Stop the running service (stays installed; returns at
                          next login or `service start`).
  status [--name N]       Show installed / running state.
```

- [ ] **Step 2: Update `README.md` getting-started**

Find the `## Getting started` section. After the daemon-start instructions, add:
```markdown
### Keep the daemon running

`kcap daemon start -d` runs until the process dies (a crash, or an OS
memory-pressure kill — macOS jetsam / Linux OOM). To have it auto-restart and
start at login, install it as a per-user service:

```bash
kcap daemon service install
```

This registers a launchd LaunchAgent (macOS), a systemd `--user` unit (Linux),
or a logon Scheduled Task (Windows). Manage it with
`kcap daemon service start|stop|status` and remove it with
`kcap daemon service uninstall`.
```

- [ ] **Step 3: Update `README.md` CLI commands → daemon section**

In the `## CLI commands` daemon subsection, add the `service` verbs to the command list (mirror the `help-daemon.txt` wording), including the note that `install` pins `KCAP_PROFILE` and captures `PATH`, and that a service-managed daemon should be stopped with `service stop`/`uninstall` rather than `daemon stop`.

- [ ] **Step 4: Verify the help renders**

Run: `dotnet run --project src/Capacitor.Cli/Capacitor.Cli.csproj -- daemon --help`
Expected: output includes the `service <action>` line and the service subcommand block.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Core/Resources/help-daemon.txt README.md
git commit -m "docs(daemon): document service install/uninstall/start/stop/status"
```

---

## Task 10: Full test run + AOT verification

**Files:** none (verification only)

- [ ] **Step 1: Run the full unit suite**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj`
Expected: all tests pass, including the new `Services/*` tests.

- [ ] **Step 2: AOT publish + warning scan (per CLAUDE.md)**

Run: `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'`
Expected: no output (no IL3050/IL2026 warnings). If any appear, they will be from `XDocument.Load` in `LaunchdServiceManager.Status` / `SystemdServiceManager` file reads — replace `XDocument.Load` with a line scan (`File.ReadLines`) if flagged; `XDocument.Parse` is used only in tests, not the publish target.

- [ ] **Step 3: Commit any AOT fixups, then summarize**

```bash
git add -A
git commit -m "test(daemon): full suite + AOT-clean for service supervisor" || echo "nothing to commit"
```

---

## Self-review notes (author)

- **Spec coverage:** install/uninstall/start/stop/status (Tasks 5,7); launchd/systemd/windows units (2,3,4,5); name sanitization + escaping (1,2,3,4); profile-pin + env capture, no `--server-url` (6,7); per-instance systemd units (3,5); idempotent install (5); `ListInstalled`/`Status.BinaryPath` for status+doctor (5,8); `ForPlatform` test seam (5); restart-on-failure vs clean-stop (2,3,4); Windows `.cmd` wrapper + `/End` (4,5); docs (9); AOT (10). The Windows `/End`-vs-restart empirical check is flagged in the spec and must be confirmed on a real Windows host during Task 5/manual QA.
- **Type consistency:** `ServiceSpec`/`ServiceState`/`ServiceStatus`/`GeneratedFile`/`ServicePlatform` defined in Task 1, used unchanged in Tasks 2-8. `ServiceText.ServiceId`/`Xml`/`CmdValue`/`SystemdValue` used consistently. Manager methods match the interface signatures.
- **Known thin/untested:** `ServiceProcess` + manager `Install/Uninstall/Start/Stop/Status/ListInstalled` shell-outs are not run in CI; their pure inputs (vectors, parsers, generators) are tested in Tasks 2-6.
