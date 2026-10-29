using System.Collections.Frozen;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Setup;
using Capacitor.Cli.Daemon.Harness.Claude;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Cli.Daemon;

public class DaemonConfig {
    public string   Name                { get; set; } = "";
    public string   ServerUrl           { get; set; } = "";
    public string[] AllowedRepoPaths    { get; set; } = [];
    public int      MaxConcurrentAgents { get; set; } = 5;

    /// <summary>The daemon's own binary as it stood at process start, the restart poll's first baseline.</summary>
    public BinaryStat? LaunchBinary { get; set; }

    /// <summary>How long a self-detected restart waits for interactive agents to reach a turn boundary.</summary>
    public TimeSpan RestartTurnWait { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How many ledgered agents a booting daemon relaunches at once.</summary>
    public int ResumeConcurrency { get; set; } = 2;

    /// <summary>A ledgered agent last alive longer ago than this is interrupted rather than resumed.</summary>
    public TimeSpan ResumeMaxAge { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Whether a booting daemon resumes its ledgered agents at all.</summary>
    public bool AutoResume { get; set; } = true;

    /// <summary>How long a resumed runtime must stay alive after publishing for the resume to count;
    /// for a PTY vendor this is the whole handshake.</summary>
    public TimeSpan ResumeReadinessWindow { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Absolute path of the native <c>kcap</c> CLI binary, resolved as the daemon's sibling
    /// (<see cref="Capacitor.Cli.Core.Mcp.KcapBinaryCommand.ResolveCliSibling"/>) — inside the
    /// daemon, <see cref="Environment.ProcessPath"/> is <c>kcap-daemon</c>, NOT the binary that
    /// generated MCP registrations point at. Used to recognize a canonical absolute-path kcap
    /// entry (e.g. <see cref="ClaudeLauncher"/>'s worktree merge-skip). Null when no
    /// sibling resolves; consumers then recognize only the literal <c>"kcap"</c>.
    /// </summary>
    public string? KcapCliPath { get; set; } = Core.Mcp.KcapBinaryCommand.ResolveCliSibling();

    /// <summary>
    /// Phase B (D3): backstop lifetime/idle bounds for a hosted review-flow reviewer, enforced
    /// in the daemon heartbeat. A reviewer whose run went terminal on the server without the daemon
    /// hearing about it (or whose driver vanished) is reaped here so it can't hold a slot forever.
    /// Defaults 6h lifetime / 2h idle; <see cref="TimeSpan.Zero"/> disables that bound. Overridden at
    /// startup from env <c>KCAP_REVIEWER_MAX_LIFETIME</c>/<c>KCAP_REVIEWER_IDLE_TIMEOUT</c> (seconds);
    /// a profile config-key form is reserved but not yet wired. Interactive agents are never touched
    /// by these.
    /// </summary>
    public TimeSpan ReviewerMaxLifetime { get; set; } = TimeSpan.FromHours(6);
    public TimeSpan ReviewerIdleTimeout { get; set; } = TimeSpan.FromHours(2);

    /// <summary>§2.7 B6 arm-A: how long a RESUMABLE hosted reviewer (app-server Codex) may sit idle
    /// between rounds before the daemon PARKS it (freeing the slot, keeping the thread for resume) —
    /// distinct from and shorter than <see cref="ReviewerIdleTimeout"/> (arm-B, the 2h hard reap).</summary>
    public TimeSpan ReviewerResumableIdleTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The daemon-local ceiling on a held ACP turn with a frozen activity seq: once <c>TurnInFlight</c>
    /// has stayed true with no further <c>Advance()</c> for longer than this, the reviewer is reaped as
    /// <c>turn_wedged</c>. Applies to EVERY review-flow reviewer, alongside
    /// <see cref="ReviewerMaxLifetime"/>/<see cref="ReviewerIdleTimeout"/> — the server-sent inactivity
    /// bound gates nothing here (see <c>AgentOrchestrator.FindReviewersToReap</c>).
    ///
    /// <para>Deliberately NOT sent by the server (spec decision 6): its equivalent
    /// <c>Flows:TurnWedgeCeilingSeconds</c> is independently defaulted on the same 60m (the
    /// <c>kcap watch</c> long-tool envelope) rather than plumbed onto the wire — this is a local safety
    /// net against an evidence-free held turn. <see cref="TimeSpan.Zero"/> disables it. Overridden at
    /// startup from env <c>KCAP_REVIEWER_TURN_WEDGE_CEILING</c> (seconds).</para>
    /// </summary>
    public TimeSpan ReviewerTurnWedgeCeiling { get; set; } = TimeSpan.FromMinutes(60);

    /// <summary>Where this daemon's files live.</summary>
    public DaemonStore Store {
        get => field ?? throw new InvalidOperationException($"DaemonConfig.Store was never set; pass a {nameof(DaemonStore)} in from the entry point.");
        set;
    }

    /// <summary>Where this daemon's kcap configuration lives — a different anchor from
    /// <see cref="Store"/>, which ignores <c>KCAP_CONFIG_DIR</c> by design.</summary>
    public ConfigRoot ConfigRoot {
        get => field ?? throw new InvalidOperationException($"DaemonConfig.ConfigRoot was never set; pass a {nameof(Core.ConfigRoot)} in from the entry point.");
        set;
    }

    /// <summary>The profile this daemon resolved at boot. A separate process from the CLI, so it
    /// resolves its own; nothing re-resolves later, which is what keeps its token reads and its
    /// daemon identity naming one profile for the daemon's whole life.</summary>
    /// <summary>The local installation's root, so every transport to the server is checked against its registry.</summary>
    public LocalRoot? LocalRoot { get; set; }

    public ProfileContext Profiles {
        get => _profiles ?? throw new InvalidOperationException($"DaemonConfig.Profiles was never set; pass a {nameof(ProfileContext)} in from the entry point.");
        set => _profiles = value;
    }

    ProfileContext? _profiles;

    /// <summary>The profile name, or null where no <see cref="ProfileContext"/> was passed in.</summary>
    public string? ProfileNameOrNull => _profiles?.Name;

    /// <summary>Why <see cref="ServerUrl"/> may not be sent to, as resolved at boot; null when it may, or when nothing
    /// was resolved.</summary>
    public string? DestinationRefusal => _profiles is null ? null : LocalDestination.Refusal(ServerUrl, _profiles.Resolution.Endpoint);

    /// <summary>Whether the resolved profile opted out of the update check; false when nothing was resolved.</summary>
    public bool UpdateCheckOff => _profiles?.Effective?.UpdateCheck == false;

    /// <summary>Phase B (D4): a fresh per-boot epoch (GUID). Written into each spawned child's
    /// <c>KCAP_DAEMON_EPOCH</c> env marker; the startup env-marker scan kills same-daemon children
    /// whose epoch differs from the current one (i.e. survivors of a prior incarnation). Null → the
    /// orchestrator generates one at construction. <c>DaemonRunner</c> pins this before building
    /// services (Phase B2-b) so the advertised connect epoch and the orchestrator's own boot epoch
    /// agree.</summary>
    public string? DaemonEpoch { get; set; }

    /// <summary>
    /// Phase B2-b (sequenced-settlement design §4.2.3): the durable coverage boot-chain verdict,
    /// folded by <c>CoverageJournal.RecordBoot</c> in <c>DaemonRunner</c> BEFORE any Connect/spawn and
    /// advertised on <c>DaemonConnect</c>. True only where OS containment leaves genuinely no recordless
    /// survivor class (the Windows Job Object); absent/false ⇒ "has a recordless class" (the server
    /// requires per-id death proof). The server consumes it only on Windows, so a Linux/macOS value is
    /// inert. Fail-closed to false when the fold/persist fails.
    /// </summary>
    public bool RecordlessSurvivorsImpossible { get; set; }

    /// <summary>
    /// Per-process GUID generated at startup, also written to the daemon's
    /// flock-file content. Sent over <c>DaemonConnect</c> so the server
    /// can tell "same daemon reconnecting" from "different daemon
    /// claiming the same name". Set in <c>DaemonRunner.RunAsync</c> once
    /// the lock has been acquired; <c>null</c> in tests that bypass lock
    /// acquisition.
    /// </summary>
    public string? InstanceId { get; set; }

    /// <summary>The instance of the boot just before this one, as the daemon lock witnessed it; null when
    /// the lock could not tell. A flow participant resumes only from that instance's planned stop.</summary>
    public string? PriorInstanceId { get; set; }

    /// <summary>
    /// Daemon binary version (<c>AssemblyInformationalVersion</c>). Sent
    /// over <c>DaemonConnect</c> and surfaced on the server's
    /// <c>Daemon connected:</c> log line + <c>DaemonInfo</c>. Set in
    /// <c>DaemonRunner.RunAsync</c>.
    /// </summary>
    public string? Version { get; set; }

    /// <summary>
    /// Vendor tokens this daemon can actually spawn — populated in
    /// <c>DaemonRunner.RunAsync</c> by probing each registered
    /// <c>IHostedAgentLauncher.IsAvailable()</c>. Sent over
    /// <c>DaemonConnect</c> so the server's launch dialog only
    /// offers vendors this daemon has installed. <c>null</c> when the
    /// host hasn't been built yet or in tests that bypass the runner.
    /// </summary>
    public string[]? SupportedVendors { get; set; }

    /// <summary>
    /// Vendor tokens this daemon can run fully unattended (a subset of
    /// <see cref="SupportedVendors"/>) — populated in
    /// <c>DaemonRunner.RunAsync</c> by probing each registered
    /// <c>IHostedAgentRuntimeFactory.IsAvailable()</c> and
    /// <c>.SupportsUnattended</c>. Sent over <c>DaemonConnect</c> so the
    /// server can gate a reviewer-vendor override on unattended capability,
    /// not merely installation. <c>null</c> when the host hasn't been built
    /// yet or in tests that bypass the runner.
    /// </summary>
    public string[]? UnattendedVendors { get; set; }

    /// <summary>Setting this keeps the Codex resolution already paired with the capabilities; a caller
    /// that computed them from a resolution publishes both through <see cref="Advertise"/>.</summary>
    public IReadOnlyList<UnattendedVendorCapability>? UnattendedVendorCapabilities {
        get => Advertisement.Capabilities;
        set => Advertisement = Advertisement with { Capabilities = value };
    }

    internal UnattendedAdvertisement Advertisement {
        get => Volatile.Read(ref _advertisement);
        private set => Volatile.Write(ref _advertisement, value);
    }

    UnattendedAdvertisement _advertisement = UnattendedAdvertisement.None;

    internal void Advertise(IReadOnlyList<UnattendedVendorCapability> capabilities, Harness.Codex.CodexTransportResolution codex) =>
        Advertisement = new(capabilities, codex);

    /// <summary>Installed vendors whose runtime can host a single-pass PR review — the server offers and
    /// admits only these for one. <c>null</c> before the runner has probed.</summary>
    public string[]? PrReviewVendors { get; set; }

    /// <summary>Per-vendor fingerprint of the binary <see cref="UnattendedVendorCapabilities"/> was
    /// probed from, taken before that probe; the vendor CLI watcher's starting point.</summary>
    public IReadOnlyDictionary<string, Services.CliBinaryStat?>? UnattendedVendorBaselines { get; set; }

    /// <summary>Per-vendor launchable models probed from the installed CLIs. Replaced by reference on
    /// every refresh, never mutated: serializers on other threads may be enumerating the current
    /// instance.</summary>
    public Dictionary<string, VendorModelOption[]>? VendorModels { get; set; }

    /// <summary>Each registered Codex account's launchable models, keyed by registry id. An unregistered
    /// default has no id, so its catalog is only in <see cref="VendorModels"/>. Replaced by reference,
    /// like <see cref="VendorModels"/>.</summary>
    public Dictionary<string, VendorModelOption[]>? VendorModelsByAccount { get; set; }

    /// <summary>Fingerprints of each vendor's catalog files, taken before the startup probe, and of each
    /// account's, taken before its latest probe; the vendor CLI watcher's starting point for an entry
    /// it begins watching. Replaced by reference.</summary>
    public IReadOnlyDictionary<string, Services.CatalogPathStat[]>? VendorCatalogBaselines { get; set; }

    /// <summary>
    /// Vendor tokens this daemon accepts a launch-time ACP permission preset for — the installed
    /// hostable vendors (a subset of <see cref="SupportedVendors"/>) that route permissions through
    /// the ACP bridge, computed INDEPENDENTLY of unattended certification (a preset is an
    /// interactive-launch feature). Sent over <c>DaemonConnect</c> so the server enables the launch
    /// dialog's preset selector and refuses a preset toward a non-advertising daemon. <c>null</c> when
    /// the host hasn't been built yet or in tests that bypass the runner.
    /// </summary>
    public string[]? AcpPresetVendors { get; set; }

    /// <summary>Vendor tokens this daemon accepts a launch-time permission mode for — Claude when it
    /// is hosted. Sent over <c>DaemonConnect</c> so the server refuses a mode toward a daemon that
    /// would ignore it. <c>null</c> until the host is built.</summary>
    public string[]? PermissionModeVendors { get; set; }

    /// <summary>The home directory this daemon resolved at boot. Every home-derived daemon path
    /// reads it, so a descendant can't be handed a different one than the entry point chose.</summary>
    public UserHome Home {
        get => field ?? throw new InvalidOperationException($"DaemonConfig.Home was never set; pass a {nameof(UserHome)} in from the entry point.");
        set;
    }

    /// <summary>The host's account registry; null leaves the accounts publisher inert.</summary>
    public AccountStore? Accounts { get; set; }

    /// <summary>The config directory each account-bearing vendor uses when no account is chosen.</summary>
    public IReadOnlyDictionary<HarnessId, string> DefaultDirectories { get; set; } = FrozenDictionary<HarnessId, string>.Empty;

    /// <summary>The PATH probe resolved at boot, the one the <c>HarnessRegistry</c> is built over. A
    /// configured vendor path is not a harness question, so its resolvers take this.</summary>
    public BinaryProbe Binaries {
        get => field ?? throw new InvalidOperationException($"DaemonConfig.Binaries was never set; pass a {nameof(BinaryProbe)} in from the entry point.");
        set;
    }

    public string WorktreeRoot { get; set; } = "";

    public string ClaudePath { get; set; } = Core.Harness.Claude.ClaudeHarness.CliBinary;
    public string CodexPath  { get; set; } = Core.Harness.Codex.CodexHarness.CliBinary;

    /// <summary>The Codex resolution this daemon routes on and advertises. Swapped whole by the
    /// capability refresh; readers on other threads take the reference once.</summary>
    internal Harness.Codex.CodexTransportResolution CodexResolution {
        get => Volatile.Read(ref _codexResolution);
        set => Volatile.Write(ref _codexResolution, value);
    }

    Harness.Codex.CodexTransportResolution _codexResolution = Harness.Codex.CodexTransportResolution.Unresolved;

    /// <summary>The last resolution a registration carried to the server. A launch that names no
    /// transport follows this one, since it is what the server dispatched against.</summary>
    internal Harness.Codex.CodexTransportResolution AcknowledgedCodexResolution {
        get => Volatile.Read(ref _acknowledgedCodexResolution);
        set => Volatile.Write(ref _acknowledgedCodexResolution, value);
    }

    Harness.Codex.CodexTransportResolution _acknowledgedCodexResolution = Harness.Codex.CodexTransportResolution.Unresolved;

    /// <summary>Records that a registration carried <paramref name="sent"/>. Only a newer generation
    /// moves it, since registrations from different paths can finish out of order.</summary>
    /// <returns>True when the acknowledgement moved.</returns>
    internal bool AcknowledgeCodex(Harness.Codex.CodexTransportResolution sent) {
        while (true) {
            var current = AcknowledgedCodexResolution;
            if (sent.Generation <= current.Generation) return false;
            if (ReferenceEquals(Interlocked.CompareExchange(ref _acknowledgedCodexResolution, sent, current), current)) return true;
        }
    }

    public long ServerAcknowledgedCodexGeneration => AcknowledgedCodexResolution.Generation;

    /// <summary>The newest Codex generation a registration attempt carried and failed to deliver. It
    /// stands for the committed resolution only while that one is still unacknowledged.</summary>
    internal long FailedCodexGeneration => Volatile.Read(ref _failedCodexGeneration);

    long _failedCodexGeneration;

    /// <returns>True when the failed generation moved.</returns>
    internal bool CodexRegistrationFailed(Harness.Codex.CodexTransportResolution sent) {
        while (true) {
            var current = FailedCodexGeneration;
            if (sent.Generation <= current) return false;
            if (Interlocked.CompareExchange(ref _failedCodexGeneration, sent.Generation, current) == current) return true;
        }
    }

    /// <summary>Why <c>experimental.json</c> could not be read on the last resolution pass; null when it was.</summary>
    internal string? ExperimentalSettingsProblem {
        get => Volatile.Read(ref _experimentalSettingsProblem);
        set => Volatile.Write(ref _experimentalSettingsProblem, value);
    }

    string? _experimentalSettingsProblem;

    /// <summary>This daemon's profile's experimental switches, read from disk now. Empty when no
    /// profile was resolved.</summary>
    public ExperimentalSettingsRead ReadExperimentalSettings() =>
        ProfileNameOrNull is { } profile ? new ExperimentalSettingsStore(ConfigRoot).Load(profile) : ExperimentalSettingsRead.Empty;

    /// <summary>Seconds an interactive hosted Codex approval (<c>*/requestApproval</c>) waits for the
    /// user before failing closed (deny). Overridable via <c>KCAP_CODEX_APPSERVER_APPROVAL_TIMEOUT_SECONDS</c>.
    /// Consumed as <c>TimeSpan.FromSeconds(Math.Max(1, …))</c>.</summary>
    public int CodexAppServerApprovalTimeoutSeconds { get; set; } = 45;

    /// <summary>
    /// Path or bare command for the Cursor CLI's ACP entry point, spawned as
    /// <c>{CursorPath} acp</c> by <c>AcpHostedAgentRuntimeFactory</c>. Overridable
    /// via <c>KCAP_CURSOR_PATH</c>, mirroring <see cref="ClaudePath"/>/<see cref="CodexPath"/>.
    /// </summary>
    public string CursorPath { get; set; } = Core.Harness.Cursor.CursorHarness.CliBinary;

    /// <summary>
    /// Family-prefix default model for Cursor ACP sessions, e.g.
    /// <c>"claude-sonnet-4-5"</c>. Cursor's wire protocol requires the exact, parameterized
    /// <c>modelId</c> from <c>session/new</c>'s <c>availableModels</c> (e.g.
    /// <c>claude-sonnet-4-5[thinking=true,context=200k]</c>), so this bare family name is resolved
    /// against that list at launch time by <c>AcpModelResolver.Resolve</c> — not sent verbatim.
    /// Overridable via <c>KCAP_CURSOR_MODEL</c>, mirroring <see cref="CursorPath"/>. A per-launch
    /// model override (<c>RuntimeStartContext.Model</c>, when the launch specifies one) takes
    /// precedence over this daemon-wide default — see <c>AcpHostedAgentRuntimeFactory</c>.
    /// </summary>
    public string CursorModel { get; set; } = "claude-sonnet-4-5";

    /// <summary>Reserved for a future AcpVendorDescriptor (this change adds the plumbing; no
    /// descriptor consumes this yet). Overridable via KCAP_COPILOT_PATH, mirroring CursorPath.</summary>
    public string CopilotPath { get; set; } = Core.Harness.Copilot.CopilotHarness.CliBinary;

    /// <summary>
    /// Path or bare command for AWS Kiro CLI's ACP entry point, spawned as <c>{KiroPath} acp</c> by
    /// <c>AcpHostedAgentRuntimeFactory</c>. Overridable via <c>KCAP_KIRO_PATH</c>, mirroring
    /// <see cref="CursorPath"/>. Only this one name is probed, so an operator whose binary is named
    /// otherwise sets the variable.
    /// </summary>
    public string KiroPath { get; set; } = Core.Harness.Kiro.KiroHarness.CliBinary;

    /// <summary>
    /// Daemon-wide default model for Kiro ACP sessions, e.g. <c>"claude-haiku-4.5"</c>, resolved
    /// against <c>session/new</c>'s <c>availableModels</c> at launch time by
    /// <c>AcpModelResolver.Resolve</c> (Kiro's ids are bare, unlike Cursor's parameterized ones,
    /// but the resolution path is shared) and applied via <c>session/set_model</c> —
    /// probe-verified at effect level (<c>docs/probes/2026-08-05-kiro-model-override/</c>).
    /// Overridable via <c>KCAP_KIRO_MODEL</c>, mirroring <see cref="CursorModel"/>.
    ///
    /// <para>Unlike <see cref="CursorModel"/> the default is NULL, deliberately: zero-configuration
    /// Kiro hosting keeps the vendor's own default model, with nothing requested and nothing
    /// reported — the behaviour Kiro hosting shipped with. A per-launch model override
    /// (<c>RuntimeStartContext.Model</c>) takes precedence over this daemon-wide default — see
    /// <c>AcpHostedAgentRuntimeFactory</c>.</para>
    /// </summary>
    public string? KiroModel { get; set; }

    /// <summary>
    /// Whether THIS daemon may run Gemini as an unattended review-flow reviewer. **Default TRUE — the
    /// variable is an opt-OUT.**
    ///
    /// <para>An unattended reviewer runs in a daemon-owned worktree with this daemon's own HOME, so
    /// repository content that steers the model into tool use gets code execution with this user's full
    /// authority — including the credentials in the token store, writes that reach other worktrees and the
    /// installed CLI, and processes that outlive the review. That is a genuine risk and it is unchanged.
    /// What changed is the recognition that a PER-VENDOR gate never addressed it: the reviewer vendor is
    /// a caller-chosen parameter, and Claude, Codex, Cursor and Copilot have the same authority with no
    /// gate at all, so anyone the gate excluded just asked for one of those.</para>
    ///
    /// <para>The build affirmation still applies: the reviewer's containment is the vendor's exact-name
    /// MCP allowlist, a behaviour of the installed build, so a build BELOW this daemon's recorded floor
    /// is refused. The floor is seeded automatically at startup so it never blocks a first launch —
    /// see <c>GeminiReviewerCapability</c>.</para>
    ///
    /// <para>Set <c>KCAP_GEMINI_UNATTENDED_REVIEWER=0</c> in the DAEMON's environment to disable, which
    /// for a supervised daemon means the service unit — <c>ServiceEnvironment</c> carries it there,
    /// since a unit inherits nothing from the installing shell.</para>
    /// </summary>
    public bool GeminiUnattendedReviewerEnabled { get; set; } = true;

    /// <summary>
    /// Whether THIS daemon may run Kiro as an unattended review-flow reviewer. **Default TRUE — the
    /// variable is an opt-OUT** (<c>KCAP_KIRO_UNATTENDED_REVIEWER=0</c> disables). A trusted read tool
    /// is not path-scoped, so a review can read every file this daemon user can and return it to the
    /// requester — true here and equally true of the never-gated Claude/Codex/Cursor/Copilot reviewers,
    /// which can additionally write and execute. See <c>KiroReviewerCapability</c>.
    /// </summary>
    public bool KiroUnattendedReviewerEnabled { get; set; } = true;

    /// <summary>
    /// One absolute budget, in seconds, for a Kiro reviewer launch: spawn through the first prompt
    /// completing. On expiry the child is terminated, its isolated home removed, and the launch fails
    /// with a coded error.
    ///
    /// <para>Not a per-stage timeout — a fresh one per stage lets a slow sequence approach a multiple
    /// of the budget. The failure it exists for is measured: an unauthenticated kiro-cli does not
    /// error, it opens a browser prompt and stays alive indefinitely.</para>
    /// </summary>
    public int KiroReviewerLaunchTimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// Path or bare command for the Antigravity CLI, spawned per turn by
    /// <c>AntigravityHostedAgentRuntimeFactory</c>. Overridable via <c>KCAP_ANTIGRAVITY_PATH</c>.
    /// Availability is <c>Binaries.Finds(AntigravityPath)</c>, so a name that is not what the install
    /// puts on PATH withdraws the vendor silently rather than failing visibly.
    /// </summary>
    public string AntigravityPath { get; set; } = Core.Harness.Antigravity.AntigravityHarness.CliBinary;

    /// <summary>Daemon-wide default model for Antigravity reviewer launches, passed as
    /// <c>--model</c>. Null leaves agy on its own default. An unknown slug makes agy hard-fail,
    /// which is a clean audit signal rather than a silent downgrade.</summary>
    public string? AntigravityModel { get; set; }

    /// <summary>Whether THIS daemon may run unattended Antigravity reviews. **Default TRUE — the
    /// variable is an opt-OUT** (<c>KCAP_ANTIGRAVITY_UNATTENDED_REVIEWER=0</c> disables), matching the
    /// never-gated Claude/Codex/Cursor/Copilot reviewers that carry the same authority.
    ///
    /// <para>The minimum <c>agy</c> build is deliberately NOT config: it is a daemon-owned record
    /// (<c>ReviewerVersionStore</c>, moved by <c>kcap daemon reviewer affirm --vendor antigravity</c>),
    /// exactly as for Kiro and Gemini. A floor an operator could set from a shell profile would be
    /// re-affirmed by their dotfiles rather than by them.</para></summary>
    public bool AntigravityUnattendedReviewerEnabled { get; set; } = true;

    /// <summary>Absolute ceiling on the FIRST turn — spawn, NDJSON handshake and auth. An
    /// unauthenticated agy can sit on an interactive OAuth wait, so this is what turns that into a
    /// bounded, coded failure.</summary>
    public int AntigravityReviewerLaunchTimeoutSeconds { get; set; } = 120;

    /// <summary>Ceiling on every SUBSEQUENT turn. Bounding only the launch would leave turn 2+
    /// unbounded, and with ReadOutputAsync parked by design nothing else would ever complete.</summary>
    public int AntigravityReviewerTurnTimeoutSeconds { get; set; } = 600;

    /// <summary>Path or bare command for SST OpenCode's ACP entry point, spawned as
    /// <c>{OpenCodePath} acp</c> by <c>AcpHostedAgentRuntimeFactory</c>. It drives interactive
    /// hosting, and availability is <c>Binaries.Finds(OpenCodePath)</c>. Overridable via
    /// <c>KCAP_OPENCODE_PATH</c>.</summary>
    public string OpenCodePath { get; set; } = Core.Harness.OpenCode.OpenCodeHarness.CliBinary;

    /// <summary>
    /// Optional daemon-wide default model for hosted OpenCode agents, resolved against
    /// <c>session/new</c>'s selectable-model list at launch time by <c>AcpModelResolver.Resolve</c>
    /// and applied via <c>session/set_config_option</c> — probe-verified at effect level
    /// (<c>docs/probes/2026-08-07-opencode-acp/</c> §2: the model self-identified as the requested id).
    /// Overridable via <c>KCAP_OPENCODE_MODEL</c>, mirroring <see cref="KiroModel"/>.
    ///
    /// <para>Ids are <c>provider/model</c> (e.g. <c>opencode/big-pickle</c>), and OpenCode publishes
    /// its list as <c>configOptions</c> rather than a <c>models</c> object — see
    /// <c>AcpSessionModelList</c>. A display label ("DeepSeek V4 Flash Free") also resolves, via the
    /// resolver's name arm.</para>
    ///
    /// <para>Like <see cref="KiroModel"/> and unlike <see cref="CursorModel"/> the default is NULL,
    /// deliberately: a zero-configuration launch keeps OpenCode's own configured default and reports
    /// no model. A per-launch <c>RuntimeStartContext.Model</c> takes precedence over this
    /// daemon-wide default.</para>
    /// </summary>
    public string? OpenCodeModel { get; set; }

    /// <summary>
    /// Whether THIS daemon may run OpenCode as an unattended review-flow reviewer. **Default TRUE — the
    /// variable is an opt-OUT** (<c>KCAP_OPENCODE_UNATTENDED_REVIEWER=0</c> disables). This is the most
    /// contained reviewer of the eight: no shell, no write, no network. Its read tools are still not
    /// path-scoped, as with every other reviewer. See <c>OpenCodeReviewerCapability</c>.
    /// </summary>
    public bool OpenCodeUnattendedReviewerEnabled { get; set; } = true;

    /// <summary>Whether this daemon may run Grok Build as an unattended review-flow reviewer. Default
    /// true; <c>KCAP_GROK_UNATTENDED_REVIEWER=0</c> disables. See <c>GrokReviewerCapability</c>.</summary>
    public bool GrokUnattendedReviewerEnabled { get; set; } = true;

    /// <summary>
    /// One absolute budget, in seconds, for an OpenCode reviewer launch: spawn through the first prompt
    /// completing. On expiry the child is terminated, its isolated config dir removed, and the launch
    /// fails with a coded error.
    ///
    /// <para>Not a per-stage timeout — a fresh one per stage lets a slow sequence approach a multiple of
    /// the budget. The failure it exists for is the same one Kiro's budget covers: an unauthenticated
    /// vendor CLI that does not error but waits on an interactive login.</para>
    /// </summary>
    public int OpenCodeReviewerLaunchTimeoutSeconds { get; set; } = 120;

    /// <summary>Path or bare command for Pi's RPC entry point, spawned as
    /// <c>{PiPath} --mode rpc</c> by <c>PiRpcHostedAgentRuntimeFactory</c> for both interactive hosting
    /// and the gated unattended reviewer. Availability is <c>Binaries.Finds(PiPath)</c>. Overridable
    /// via <c>KCAP_PI_PATH</c>.</summary>
    public string PiPath { get; set; } = Core.Harness.Pi.PiHarness.CliBinary;

    /// <summary>
    /// Optional daemon-wide default model for hosted Pi agents, passed as <c>--model</c> on the
    /// spawned <c>pi --mode rpc</c> child. Overridable via <c>KCAP_PI_MODEL</c>, mirroring
    /// <see cref="OpenCodeModel"/>.
    ///
    /// <para>Like <see cref="OpenCodeModel"/> and <see cref="KiroModel"/> the default is NULL,
    /// deliberately: a zero-configuration launch keeps Pi's own configured default and reports no
    /// model. A per-launch <c>RuntimeStartContext.Model</c> takes precedence over this daemon-wide
    /// default (the <c>"default"</c> sentinel falls through to it, same convention as every other
    /// vendor's <c>ResolveModel</c>).</para>
    /// </summary>
    public string? PiModel { get; set; }

    /// <summary>Whether THIS daemon may run Pi as an unattended review-flow reviewer. **Default TRUE —
    /// the variable is an opt-OUT** (<c>KCAP_PI_UNATTENDED_REVIEWER=0</c> disables), matching the other
    /// gated reviewers and the never-gated Claude/Codex/Cursor/Copilot ones that carry the same
    /// authority. See <c>PiReviewerCapability</c>.</summary>
    public bool PiUnattendedReviewerEnabled { get; set; } = true;

    /// <summary>How long a Pi reviewer round may go without a frame from Pi before it is reaped.</summary>
    public int PiReviewerTurnTimeoutSeconds { get; set; } = 600;

    /// <summary>Path or bare command for Google Gemini CLI's ACP entry point, spawned as
    /// <c>{GeminiPath} --experimental-acp …</c> by <c>AcpHostedAgentRuntimeFactory</c>. It drives
    /// interactive hosting AND the gated unattended reviewer, whose build-affirmation probe reads
    /// whichever binary this names. Overridable via <c>KCAP_GEMINI_PATH</c>.</summary>
    public string GeminiPath { get; set; } = Core.Harness.Gemini.GeminiHarness.CliBinary;

    /// <summary>Path or bare command for the Mistral Vibe CLI. Overridable via <c>KCAP_MISTRAL_VIBE_PATH</c>.
    /// Vibe is a recording-only harness — the daemon never launches it — so this exists only to give
    /// <c>kcap config</c> a slot for the binary path; it is not read by any launcher.</summary>
    public string MistralVibePath { get; set; } = Core.Harness.MistralVibe.MistralVibeHarness.CliBinary;

    /// <summary>Path or bare command for Grok Build. Overridable via <c>KCAP_GROK_PATH</c>.</summary>
    public string GrokPath { get; set; } = Core.Harness.Grok.GrokHarness.CliBinary;

    /// <summary>Path or bare command for the Muse Code CLI. Overridable via <c>KCAP_MUSE_PATH</c>.</summary>
    public string MusePath { get; set; } = Core.Harness.Muse.MuseHarness.CliBinary;

    /// <summary>Daemon-wide default model for Muse; null keeps Muse's own default. Overridable via
    /// <c>KCAP_MUSE_MODEL</c>.</summary>
    public string? MuseModel { get; set; }

    /// <summary>Whether THIS daemon may run Muse as an unattended review-flow reviewer. Default TRUE;
    /// <c>KCAP_MUSE_UNATTENDED_REVIEWER=0</c> opts out, as for every gated reviewer.</summary>
    public bool MuseUnattendedReviewerEnabled { get; set; } = true;

    /// <summary>
    /// Opt-in, off-by-default ACP wire/content debug logging (<c>KCAP_ACP_DEBUG_FRAMES</c>). When
    /// <see langword="false"/> (the default), the ACP layers (<c>AcpEventTranslator</c>,
    /// <c>AcpChildProcess</c>, <c>AcpConnection</c>) log shape/length only for the traffic that would
    /// otherwise carry prompt/tool/file content — an unrecognized <c>session/update</c> kind, raw
    /// <c>cursor-agent</c> stderr lines, and full inbound/outbound JSON-RPC frames. When
    /// <see langword="true"/>, those same call sites log full (length-capped) content at Debug for
    /// local troubleshooting — never sent to the server, never written to the transcript. Read from
    /// the env var in <c>DaemonRunner.RunAsync</c>, which also emits a one-time startup Warning when
    /// this is on, since the logged content may include sensitive payloads.
    /// </summary>
    public bool DebugFrames { get; set; }

    /// <summary>
    /// Kill switch for ACP hosted-agent crash reconnect/resume (<c>KCAP_ACP_RECONNECT</c>). Default
    /// ON; set <c>0</c>/<c>false</c> to disable globally. When off — or for a vendor whose
    /// descriptor is not probe-verified reconnect-capable, a launch that is a review flow, or a
    /// session whose handshake didn't advertise <c>loadSession</c> — a child-process death keeps
    /// today's behavior byte-for-byte: the read loop ends and the agent finalizes. Deliberately the
    /// only knob: attempts, backoff, and the per-session resume cap are fixed constants
    /// (reconnect spec §4).
    /// </summary>
    public bool AcpReconnectEnabled { get; set; } = true;

    /// <summary>
    /// Path to the kcap CLI binary. Used by the daemon to spawn auxiliary
    /// processes (e.g. <c>generate-whats-done</c>) when claude didn't fire its
    /// own session-end hook. Defaults to "kcap" — resolved via PATH, which
    /// works for npm installs that place both <c>kcap</c> and
    /// <c>kcap-daemon</c> in <c>node_modules/.bin</c>.
    /// </summary>
    public string CapacitorPath { get; set; } = "kcap";

    /// <summary>The argv the daemon was launched with, captured for self-respawn (detached restart).</summary>
    public IReadOnlyList<string> OriginalArgs { get; set; } = [];

    /// <summary>
    /// Raw value of <c>KCAP_CONSENT_SEED_DEFAULT</c>, captured off ambient env at boot by
    /// <see cref="DaemonRunner.CaptureBootCarriers"/> and immediately removed from the process
    /// environment so no descendant (PTY-spawned agent, ACP child) can observe it by inheritance.
    /// Unvalidated here — <see cref="DaemonRunner.RunBootChecksAsync"/> classifies/validates this
    /// directive via <c>LaunchConsentStore.BootSeed</c>.
    /// </summary>
    public string? ConsentSeedDirective { get; set; }

    /// <summary>
    /// Raw value of <c>KCAP_EXPECT_SERVER_URL</c>, captured/removed the same way as
    /// <see cref="ConsentSeedDirective"/>. Unvalidated here — <see cref="DaemonRunner.RunBootChecksAsync"/>
    /// checks it against the resolved <see cref="ServerUrl"/>.
    /// </summary>
    public string? ExpectedServerUrl { get; set; }

    /// <summary>
    /// Raw value of <c>KCAP_BOOT_ATTEMPT</c>, captured/removed the same way as
    /// <see cref="ConsentSeedDirective"/>. Per-launch-action: NOT re-injected into a self-respawned
    /// successor (see <c>DetachedRespawnStrategy.SuccessorEnvOverlay</c>) — a self-respawn is not
    /// the app's own action.
    /// </summary>
    public string? BootAttemptId { get; set; }

    public List<string> Validate() {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(ServerUrl)) {
            errors.Add("ServerUrl is required");
        } else if (!Uri.TryCreate(ServerUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) {
            errors.Add($"ServerUrl must be a valid http/https URL, got: {UnusableUrlDiagnostic.Sanitize(ServerUrl)}");
        }

        if (MaxConcurrentAgents < 0) {
            errors.Add("MaxConcurrentAgents must be 0 (unlimited) or a positive number");
        }

        if (string.IsNullOrWhiteSpace(WorktreeRoot)) {
            errors.Add("WorktreeRoot is required");
        }

        return errors;
    }

    public bool IsRepoAllowed(string repoPath) {
        if (AllowedRepoPaths.Length == 0) {
            return true;
        }

        // Compare with forward slashes so an operator's "/*" wildcard and prefix matching work
        // regardless of the host's native separator (Windows canonical paths use '\'). No-op on POSIX.
        var path = repoPath.Replace('\\', '/');

        return AllowedRepoPaths.Any(raw => {
                var pattern = raw.Replace('\\', '/');

                if (pattern.EndsWith("/*")) {
                    var prefix = pattern[..^1]; // keep trailing slash: "/allowed/"

                    return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || string.Equals(path, pattern[..^2], StringComparison.OrdinalIgnoreCase);
                }

                return string.Equals(path, pattern, StringComparison.OrdinalIgnoreCase);
            }
        );
    }
}
