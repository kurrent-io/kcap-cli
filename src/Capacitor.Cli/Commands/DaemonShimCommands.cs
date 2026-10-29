using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.FirstRun;
using Capacitor.Cli.Core.Install;
using Capacitor.Cli.Core.Setup;

namespace Capacitor.Cli.Commands;

/// <summary>
/// The decision <c>kcap daemon shim ensure</c> makes from a fresh login-shell probe: whether the
/// terminal already resolves <c>kcap</c> (the flow's done state), whether the shim should be
/// installed, or whether to fail closed with a coded reason. Pure — no I/O — so the ladder is
/// directly testable. Every ambiguous row fails closed, never guessed.
/// </summary>
internal enum ShimEnsureAction {
    /// <summary>kcap already resolves from the terminal — nothing to do.</summary>
    AlreadyOnPath,

    /// <summary>Probe positively found no kcap; this OS's PATH installer is the fix.</summary>
    Install,

    /// <summary>Nothing was mutated; <see cref="ShimEnsureDecision.Reason"/> names the row.</summary>
    Refuse,
}

/// <summary>One ladder classification. <see cref="Reason"/> is non-null only for
/// <see cref="ShimEnsureAction.Refuse"/> — a coded token naming the row, never prose.</summary>
internal readonly record struct ShimEnsureDecision(ShimEnsureAction Action, string? Reason = null);

/// <summary>
/// Pure ladder classifier for the PATH fix: only a POSITIVE probe finding no <c>kcap</c> on the
/// terminal PATH, on an OS with an installer, is installable. A positive "already on PATH" is
/// terminal on any platform. Where there is no installer the platform refusal beats an unknown
/// probe, so the flow sees a stable <c>unsupported_platform</c> row rather than a probe-dependent one.
/// </summary>
internal static class ShimEnsureClassifier {
    public static ShimEnsureDecision Classify(bool? onPath, bool installable) {
        if (onPath == true)  return new ShimEnsureDecision(ShimEnsureAction.AlreadyOnPath);
        if (!installable)    return new ShimEnsureDecision(ShimEnsureAction.Refuse, FirstRunMachineActionReasons.UnsupportedPlatform);
        if (onPath is null)  return new ShimEnsureDecision(ShimEnsureAction.Refuse, FirstRunMachineActionReasons.ProbeUnknown);
        return new ShimEnsureDecision(ShimEnsureAction.Install);
    }
}

/// <summary>
/// <c>kcap daemon shim ensure</c> — the flow's named PATH-fix capability. The CLI composes the
/// whole operation itself: it resolves its own binary path (never a server-supplied path), probes
/// the terminal PATH, and on a positive absence installs through <see cref="CliPathInstallers"/>:
/// a symlink in a PATH folder the user can write (else <c>~/.local/bin</c>) on macOS and Linux, a
/// user PATH entry on Windows, each followed by a re-probe; nothing asks for an admin password. Machine-readable result
/// via <c>--json</c>; human output otherwise. Exit 0 only when the terminal now resolves
/// <c>kcap</c>; every refusal and every not-on-path outcome exits non-zero with a coded token.
/// </summary>
public static class DaemonShimCommands {
    public const string Capability = FirstRunMachineCapabilities.PathShim;

    public static async Task<int> DispatchAsync(string[] args, TimeProvider time) {
        if (args.Length == 0) return Usage();

        return args[0] switch {
            "ensure" => await Ensure(args[1..], time),
            _        => Usage(),
        };
    }

    /// <param name="resolveTarget">Test seam: the shim link target. Production resolves via
    /// <see cref="ResolveLinkTarget"/> — the npm launcher when this CLI is part of an npm-global
    /// install, else the running binary. The server never supplies one.</param>
    /// <param name="probe">Test seam for the terminal PATH probe.</param>
    /// <param name="install">Test seam for the install.</param>
    /// <param name="preflight">Test seam for the installer's preflight that turns a failed install
    /// into the conflict row when a non-target entry sits at the destination.</param>
    /// <param name="installable">Test seam for whether this OS has an installer; null resolves to
    /// the real OS.</param>
    internal static async Task<int> Ensure(string[] args, TimeProvider time, Func<string?>? resolveTarget = null,
            ILoginShellProbe? probe = null, Func<string, CancellationToken, Task<ShimResult>>? install = null,
            Func<string, ShimPreflight>? preflight = null, bool? installable = null) {
        // Only --json is a legal flag; anything else (including --help and typos) is rejected
        // before the first probe or prompt — this verb's own ladder principle is fail-closed, and
        // an unknown flag must not be silently ignored into a mutation. (-h/--help never reach
        // here: Program.cs intercepts them for the whole command group.)
        if (args.Any(a => a != "--json"))
            return Usage();

        var json   = args.Contains("--json");
        var result = await EvaluateAsync(time, resolveTarget, probe, install, preflight, installable,
            disclose: json ? Console.Error.WriteLine : Console.Out.WriteLine);

        return await Report(result, json);
    }

    /// <summary>
    /// The ladder, with no console output and no exit code — what the browser flow drives through
    /// <c>IFirstRunMachineActions</c>.
    ///
    /// <para><b>The one place the operation is composed.</b> Both callers reach the machine through here,
    /// so the flow cannot end up running a different ladder from the one the verb documents, and the
    /// outcome the browser renders is the outcome the terminal would have printed.</para>
    /// </summary>
    internal static async Task<ShimEnsureJson> EvaluateAsync(
            TimeProvider time, Func<string?>? resolveTarget = null, ILoginShellProbe? probe = null,
            Func<string, CancellationToken, Task<ShimResult>>? install = null,
            Func<string, ShimPreflight>? preflight = null, bool? installable = null,
            Action<string>? disclose = null, CancellationToken ct = default) {
        var target = (resolveTarget ?? (() => ResolveLinkTarget(
            () => Environment.ProcessPath is { } exe ? InstallPaths.Stabilize(exe) : null, File.Exists,
            OperatingSystem.IsWindows())))();

        if (string.IsNullOrEmpty(target))
            return new ShimEnsureJson(Capability, null, null, null, "none",
                FirstRunMachineActionOutcomes.Refused, FirstRunMachineActionReasons.NoCliPath);

        var runner = new ProcessRunner(time);
        probe ??= TerminalPathProbe.Create(runner, Environment.GetEnvironmentVariable);
        var installer = CliPathInstallers.Create(probe, UserHome.FromEnvironment());
        var onPath = await probe.KcapOnPathAsync(ct).ConfigureAwait(false);

        var decision = ShimEnsureClassifier.Classify(onPath, installable ?? installer is not null);

        return decision.Action switch {
            ShimEnsureAction.AlreadyOnPath =>
                new ShimEnsureJson(Capability, target, onPath != null, onPath, "none",
                    FirstRunMachineActionOutcomes.AlreadyOnPath),

            ShimEnsureAction.Refuse =>
                new ShimEnsureJson(Capability, target, onPath != null, onPath, "none",
                    FirstRunMachineActionOutcomes.Refused, decision.Reason),

            _ => await Install(target, installer, install, preflight, disclose, ct),
        };
    }

    /// <summary>
    /// The shim's link target: the npm launcher when this CLI is part of an npm-global install,
    /// else the running binary itself.
    ///
    /// <para>In the npm topology the process running this code is the platform NativeAOT binary
    /// that <c>npm/kcap/bin/kcap.js</c> spawned, so <see cref="Environment.ProcessPath"/> resolves
    /// to that binary — but linking <c>/usr/local/bin/kcap</c> to it would bypass the launcher for
    /// every subsequent command. <c>kcap.js</c> is the component that intercepts <c>kcap update</c>
    /// and runs npm; the native binary's own update path only prints "Run kcap update" when invoked
    /// directly. The launcher is a sibling package (<c>@kurrent/kcap</c> vs the platform package
    /// <c>@kurrent/kcap-&lt;platform&gt;</c>), so its path is derivable from the running binary's
    /// own location with no environment lookup. When the derived launcher is absent (dev build,
    /// standalone binary), the running image is the target.</para>
    ///
    /// <para>npm on Windows nests the platform package under the wrapper's own node_modules, and
    /// <c>kcap.js</c> cannot run from PATH there: the target is the <c>kcap.cmd</c> npm wrote beside
    /// the prefix's node_modules, which runs the launcher.</para>
    /// </summary>
    internal static string? ResolveLinkTarget(Func<string?> processPath, Func<string, bool> fileExists, bool windows = false) {
        var native = processPath();
        if (string.IsNullOrEmpty(native)) return null;
        if (windows) return NpmPrefixShim(native, fileExists) ?? native;

        // .../node_modules/@kurrent/kcap-<platform>/bin/kcap  →  launcher:
        // .../node_modules/@kurrent/kcap/bin/kcap.js
        // From the platform package's bin dir: up 3 (bin → <platform> → @kurrent → node_modules),
        // then the wrapper package's launcher.
        var launcher = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(native) ?? string.Empty,
            "..", "..", "..", "@kurrent", "kcap", "bin", "kcap.js"));
        return fileExists(launcher) ? launcher : native;
    }

    static string? NpmPrefixShim(string native, Func<string, bool> fileExists) {
        for (var dir = Path.GetDirectoryName(native); !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir)) {
            if (!string.Equals(Path.GetFileName(dir), "node_modules", StringComparison.OrdinalIgnoreCase)) continue;
            if (Path.GetDirectoryName(dir) is not { Length: > 0 } prefix) return null;
            var shim = Path.Combine(prefix, "kcap.cmd");
            if (fileExists(shim) && fileExists(Path.Combine(dir, "@kurrent", "kcap", "bin", "kcap.js"))) return shim;
        }
        return null;
    }

    static async Task<ShimEnsureJson> Install(string target, ICliPathInstaller? installer,
            Func<string, CancellationToken, Task<ShimResult>>? install, Func<string, ShimPreflight>? preflight,
            Action<string>? disclose, CancellationToken ct) {
        // The installer tries a writable PATH folder first, so a foreign entry at its fallback link is
        // not a refusal until that fails; after a failed install it is the coded conflict row.
        preflight ??= installer!.Preflight;
        if (installer is not null) disclose?.Invoke(installer.Disclosure);

        var result = install is not null
            ? await install(target, ct).ConfigureAwait(false)
            : await installer!.InstallAsync(target, ct).ConfigureAwait(false);

        if (result.Outcome == ShimOutcome.Failed && preflight(target) == ShimPreflight.Conflict)
            return ConflictRefusal(target, result.Detail);

        return result.Outcome switch {
            ShimOutcome.Installed =>
                new ShimEnsureJson(Capability, target, true, true, "install",
                    FirstRunMachineActionOutcomes.Installed),

            ShimOutcome.InstalledButNotOnPath =>
                new ShimEnsureJson(Capability, target, true, false, "install",
                    FirstRunMachineActionOutcomes.InstalledNotOnPath, Detail: result.Detail),

            _ =>
                new ShimEnsureJson(Capability, target, true, null, "install",
                    FirstRunMachineActionOutcomes.Failed,
                    Detail: result.Detail),
        };
    }

    static ShimEnsureJson ConflictRefusal(string target, string? detail) =>
        new(Capability, target, true, false, "install",
            FirstRunMachineActionOutcomes.Refused, FirstRunMachineActionReasons.Conflict,
            detail ?? "a different filesystem entry already exists where kcap would be linked and was left untouched");

    static async Task<int> Report(ShimEnsureJson dto, bool json) {
        var exit = dto.ExitCode;

        if (json) {
            await Console.Out.WriteLineAsync(JsonSerializer.Serialize(dto, ShimJsonContext.Default.ShimEnsureJson));
            return exit;
        }

        switch (dto.Outcome) {
            case FirstRunMachineActionOutcomes.AlreadyOnPath:
                await Console.Out.WriteLineAsync("kcap is already on your terminal PATH.");
                return 0;
            case FirstRunMachineActionOutcomes.Installed:
                // Not "Linked … to …" — the AlreadyInstalled preflight row reaches here without
                // creating anything, so claiming a fresh link would be a lie.
                await Console.Out.WriteLineAsync("kcap is now on your terminal PATH.");
                return 0;
            case FirstRunMachineActionOutcomes.InstalledNotOnPath:
                await Console.Out.WriteLineAsync(Sanitize(dto.Detail));
                return 1;
            case FirstRunMachineActionOutcomes.Failed:
                await Console.Error.WriteLineAsync(Sanitize(dto.Detail) ?? "Installing the command-line tool failed.");
                return 1;
            default: // refused
                await Console.Error.WriteLineAsync(dto.Reason switch {
                    FirstRunMachineActionReasons.NoCliPath => "Could not resolve this CLI's own path.",
                    FirstRunMachineActionReasons.ProbeUnknown =>
                        "Could not determine whether kcap is on your terminal PATH.",
                    FirstRunMachineActionReasons.UnsupportedPlatform =>
                        "The command-line tool install is only available on macOS and Windows.",
                    FirstRunMachineActionReasons.Conflict => Sanitize(dto.Detail),
                    _                                    => "Path fix refused.",
                });
                return 1;
        }
    }

    /// Human output is terminal-bound; a macOS path or shell error text may legally carry ESC /
    /// other control bytes, which could inject ANSI sequences into the operator's terminal. The
    /// flow consumes the JSON arm (already escaped by System.Text.Json), so this only guards the
    /// console lines. Newlines/tabs are preserved — the Detail's own line breaks are intended.
    static string? Sanitize(string? text) {
        if (text is null) return null;
        return new string(text.Where(c => !char.IsControl(c) || c is '\n' or '\t').ToArray());
    }

    static int Usage() {
        Console.Error.WriteLine("Usage: kcap daemon shim <ensure> [--json]");
        Console.Error.WriteLine();
        Console.Error.WriteLine("  ensure [--json]   Probe the terminal PATH; if kcap is absent, link kcap");
        Console.Error.WriteLine("                    to this CLI in a writable PATH folder, else");
        Console.Error.WriteLine("                    /usr/local/bin (macOS; prompts once for your admin");
        Console.Error.WriteLine("                    password, or without admin rights links ~/.local/bin/kcap");
        Console.Error.WriteLine("                    and adds that folder to PATH in your shell profile), or");
        Console.Error.WriteLine("                    add its folder to your user PATH (Windows).");
        return 1;
    }
}
