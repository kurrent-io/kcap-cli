using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace Capacitor.Cli.Core.Telemetry;

/// <summary>
/// The only telemetry surface call sites touch. Every method swallows every exception:
/// an exception escaping to the NativeAOT runtime aborts the process (see Program.cs), so a
/// telemetry bug must never become a crash-on-every-command regression.
///
/// <para>One facade per process entry, constructed by <see cref="Start(TelemetryStartup, ConfigRoot)"/> and injected. A process
/// that starts a second one — an MCP server re-deriving its startup under the reportable
/// <c>mcp-server</c> pseudo-command — passes the same <see cref="TelemetryStartup"/> forward, so
/// suppression and the resolved server travel as values rather than as state the first facade left
/// behind.</para>
/// </summary>
public sealed class CliTelemetry {
    const string Endpoint = "https://phog.kurrent.io";
    const string Token    = "phc_DeHBgHGersY4LmDlADnPrsCPOAmMO7QFOH8f4DVEVmD";

    static readonly TimeSpan FlushBudget = TimeSpan.FromSeconds(1.5);

    readonly ITelemetrySink _sink;
    readonly string         _command;
    readonly string?        _deviceId;
    readonly string?        _orgGroup;
    readonly bool           _debug;

    /// <summary>
    /// Guards <see cref="_shared"/>, which is written after construction as well as during it: the
    /// loopback browser's background wait merges the returned web identity whenever the browser
    /// comes back, and that moment coincides with the foreground funnel by design, since sign-in
    /// reports completion immediately after the browser call returns.
    /// <para>Unsynchronised, an insert during a capture's read faults the read, the exception is
    /// swallowed on the way out (telemetry must never throw), and the event silently never
    /// appears. Reproduced, and it takes the event the feature exists to measure.</para>
    /// </summary>
    readonly object _sharedGate = new();

    readonly JsonObject _shared;

    CliTelemetry(
            ITelemetrySink sink, string command, bool enabled,
            string? deviceId, string? orgGroup, bool debug, JsonObject shared) {
        _sink     = sink;
        _command  = command;
        _deviceId = deviceId;
        _orgGroup = orgGroup;
        _debug    = debug;
        _shared   = shared;
        Enabled   = enabled;
        Join      = new SetupJoin(this);
        Funnel    = new SetupFunnel(this);
    }

    public bool Enabled { get; private set; }

    /// <summary>The correlation key for this run, minted for the interactive auth commands.</summary>
    public SetupJoin Join { get; }

    /// <summary>The signup funnel's event vocabulary, for the setup and login lanes.</summary>
    public SetupFunnel Funnel { get; }

    /// <summary>A facade that is off: nothing resolved, nothing minted, nothing captured.</summary>
    public static CliTelemetry Disabled() =>
        new(new NullTelemetrySink(), command: "", enabled: false,
            deviceId: null, orgGroup: null, debug: false, new JsonObject());

    /// <summary>
    /// Resolves the opt-out decision, mints the device id and builds the shared property bag,
    /// returning a facade that is either live or <see cref="Disabled"/>.
    ///
    /// <para>Reaches no console and captures nothing: the one-time privacy notice and
    /// <c>cli_first_run</c> belong to <see cref="Announce"/>, which the caller invokes once the
    /// shared bag is complete. Keeping them apart is what lets a container resolve this without
    /// printing a disclosure or consuming a once-per-device marker.</para>
    /// </summary>
    public static CliTelemetry Start(TelemetryStartup startup, ConfigRoot config) =>
        Start(startup, config,
            () => new TelemetryClient(new HttpClientHandler(), Spool(config), Token, Endpoint));

    /// <param name="sink">
    /// Invoked only when the facade comes up live, so a run that is opted out builds no HTTP
    /// handler and touches no spool. Internal because the endpoint a run ships to is this class's
    /// to decide, not a caller's; the test assemblies reach it through their grant.
    /// </param>
    internal static CliTelemetry Start(TelemetryStartup startup, ConfigRoot config, Func<ITelemetrySink> sink) {
        try {
            // An app-spawned child: no notice, no device id, no events.
            if (startup.Suppressed) return Disabled();

            var enabled = TelemetrySettings.Resolve(TelemetryState.PersistedEnabled(config)).Enabled
                       && CommandEvents.IsReportable(startup.Command);
            if (!enabled) return Disabled();

            var version = Version();

            // A device id that can't be persisted still gets an in-memory-only id for this
            // process, rather than disabling telemetry outright: silently going dark on a disk
            // hiccup costs more in data quality than a marginally inflated unique-device count in
            // this rare fallback case.
            var telemetry = new CliTelemetry(
                sink(),
                startup.Command,
                enabled: true,
                TelemetryDeviceId.GetOrCreate(config) ?? Guid.NewGuid().ToString("N"),
                PostHogPayload.OrgGroup(startup.ServerUrl),
                startup.Debug,
                new JsonObject {
                    ["source"]        = "cli",
                    ["cli_version"]   = version,
                    ["build_channel"] = TelemetryEnvironment.BuildChannel(version),
                    ["os"]            = OS(),
                    ["arch"]          = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
                    ["is_ci"]         = TelemetryEnvironment.IsCi(),
                    ["is_headless"]   = Auth.HeadlessEnvironment.IsHeadless(),
                    ["has_server"]    = startup.ServerUrl is not null,
                });

            // Minted here rather than in Announce so the key is in the shared bag before ANY event
            // can be captured — cli_first_run included, which is once per device and unrepairable by
            // a later run. Gated to the two interactive auth commands so it never attaches to a
            // recap or an import, which have no auth run to correlate, and minted ahead of any lane
            // the command later chooses so every setup/login lane carries it.
            if (startup.Command is "setup" or "login")
                telemetry.Join.Mint();

            return telemetry;
        } catch {
            return Disabled();
        }
    }

    /// <summary>Queue an event for the exit flush.</summary>
    public void Capture(string name, JsonObject properties) {
        try {
            if (!Enabled) return;

            lock (_sharedGate)
                foreach (var (key, value) in _shared)
                    properties[key] ??= value?.DeepClone();

            var e = new TelemetryEvent(name, properties, DateTimeOffset.UtcNow);

            if (_debug) Console.Error.WriteLine($"[telemetry] {name} {DebugRender(properties)}");

            _sink.Enqueue(e);
        } catch { }
    }

    /// <summary>
    /// Attach a property to every event this facade captures from here on. Swallowing like
    /// everything else in this class.
    /// </summary>
    public void AddSharedProperty(string name, JsonNode? value) {
        try {
            if (!Enabled) return;

            lock (_sharedGate) _shared[name] = value;
        } catch { }
    }

    /// <summary>
    /// Attach several properties as ONE step, so no event can be captured carrying half of them.
    /// The returned web identity arrives as a set — an arm and one id per host — and an event
    /// holding one host's id while the other is still missing would read as a real absence rather
    /// than a moment mid-merge.
    /// </summary>
    public void AddSharedProperties(JsonObject properties) {
        try {
            if (!Enabled) return;

            lock (_sharedGate)
                foreach (var (name, value) in properties)
                    _shared[name] = value?.DeepClone();
        } catch { }
    }

    /// <summary>
    /// Queue an event and flush immediately, rather than leaving it for the exit-time flush.
    /// Deliberately sync-over-async: setup funnel steps must reach PostHog before an abandoned
    /// run dies — the population being measured is people who quit mid-setup and never run kcap
    /// again, so a deferred event is a lost event, not a delayed one. Safe here because this is a
    /// console app with no SynchronizationContext to deadlock against; do not convert to
    /// fire-and-forget.
    /// </summary>
    public void CaptureNow(string name, JsonObject properties) {
        Capture(name, properties);
        FlushAndClose().GetAwaiter().GetResult();
    }

    public void RecordCommand(string command, string[] args, int exitCode, long durationMs) {
        try {
            if (!Enabled || !CommandEvents.IsReportable(command)) return;

            var props = new JsonObject {
                ["command"]     = CommandEvents.ReportableCommand(command),
                ["exit_code"]   = exitCode,
                ["duration_ms"] = durationMs,
            };

            if (CommandEvents.Subcommand(command, args) is { } sub) props["subcommand"] = sub;

            var flags = CommandEvents.Flags(args);
            if (flags.Length > 0) {
                var arr = new JsonArray();
                foreach (var f in flags) {
                    // Not a collection expression, and not arr.Add(f) either: JsonArray.Add<T>(T)
                    // binds whenever the argument's static type is narrower than JsonNode?, which
                    // "f: string" is — so a bare Add(f) still pulls in the AOT-unsafe generic
                    // overload. Only an argument statically typed JsonNode? selects the plain
                    // Add(JsonNode?) instance method.
                    JsonNode? node = JsonValue.Create(f);
                    arr.Add(node);
                }
                props["flags"] = arr;
            }

            Capture("cli_command", props);
        } catch { }
    }

    public async Task FlushAndClose() {
        try {
            if (!Enabled || _deviceId is null) return;

            await _sink.FlushAsync(_deviceId, _orgGroup, FlushBudget);
        } catch { }
    }

    /// <summary>
    /// Tears telemetry down in THIS process the instant the persisted flag flips to false.
    /// Program.cs starts the facade before any command handler runs, so by the time
    /// `kcap config set telemetry off` executes, telemetry has already resolved enabled (no file
    /// on a fresh machine), minted a device id, and possibly queued <c>cli_first_run</c> — the
    /// persisted flag alone would not stop THIS process's own ProcessExit flush from shipping it.
    /// Called from <c>ConfigCommand.TryApplyTelemetry</c> right after
    /// <see cref="TelemetryState.SetEnabled"/> persists the "off" (which already clears the
    /// on-disk id — this drops the queue it was minted for).
    /// </summary>
    public void DiscardAndDisable() {
        try {
            _sink.Discard();
            Enabled = false;
        } catch { }
    }

    /// <summary>
    /// Shows the one-time privacy notice and queues <c>cli_first_run</c>. Called once the shared bag
    /// is complete, because <c>cli_first_run</c> is captured here and is once per device — a
    /// property missing from it is missing for good.
    ///
    /// <para>"mcp-server" is the pseudo-command long-lived MCP servers start under, on top of the
    /// denylisted "mcp" (see <see cref="McpTelemetry"/>) — an agent-spawned, non-interactive process
    /// whose stderr no human is watching. kcap-memory/-sessions/-flows/-review auto-register and
    /// spawn on every agent session, so on a fresh machine one of them is plausibly the very first
    /// kcap-family process ever run. Letting the notice fire there would print the disclosure into a
    /// void AND consume the once-per-device marker, so no human-invoked command would ever show it —
    /// silently reproducing the silent-by-default posture this feature exists to avoid. Refused here
    /// rather than at the call site, so a new server cannot consume it by forgetting.</para>
    /// </summary>
    public void Announce(ConfigRoot config) {
        try {
            if (!Enabled || _command == "mcp-server") return;
            if (TelemetryState.Read(config).NoticeShown) return;

            Console.Error.WriteLine(
                "kcap collects pseudonymous usage data — command and flag names only, never argument values,");
            Console.Error.WriteLine(
                "file paths, or transcript content. It can be associated with your workspace and its creator.");
            Console.Error.WriteLine(
                "Opt out: kcap config set telemetry off (or DO_NOT_TRACK=1).");
            Console.Error.WriteLine("https://capacitor.kurrent.io/privacy");

            TelemetryState.MarkNoticeShown(config);
            Capture("cli_first_run", new JsonObject());
        } catch { }
    }

    /// <summary>
    /// Debug render with the join key redacted. <see cref="Capture"/> prints the whole merged
    /// properties object, so without this a <c>KCAP_TELEMETRY_DEBUG=1</c> run would emit the
    /// bridge key to stderr on every <c>cli_*</c> event — and an access log holding that key is a
    /// copy of the join.
    /// <para>A placeholder rather than omission: the useful debug signal is WHETHER the property
    /// is attached, which the placeholder shows. The value itself is only ever needed in PostHog,
    /// where it legitimately lives.</para>
    /// </summary>
    static string DebugRender(JsonObject properties) {
        if (properties[SetupJoin.PropertyName] is null) return properties.ToJsonString();

        var redacted = properties.DeepClone().AsObject();
        redacted[SetupJoin.PropertyName] = "[set]";

        return redacted.ToJsonString();
    }

    static TelemetrySpool Spool(ConfigRoot config) => new(config.Path("telemetry-spool.jsonl"));

    static string Version() =>
        typeof(CliTelemetry).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "unknown";

    static string OS() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "windows"
        : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)   ? "macos"
        : RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "linux"
        : "other";
}
