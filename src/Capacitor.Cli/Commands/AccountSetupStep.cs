using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Harness.Codex;
using Spectre.Console;

namespace Capacitor.Cli.Commands;

internal sealed class AccountSetupStep(AccountStore accounts, PluginEnvironment env, TimeProvider time) {
    internal void Run(CodingAgentsStep.Options options, bool interactive, Func<string, bool> promptYesNo, Action<string> writeLine) {
        if (!options.InstallAgents) return;

        try {
            Adopt(options, interactive, promptYesNo, writeLine);
        } catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException
                                         or TimeoutException or WaitHandleCannotBeOpenedException) {
            writeLine($"  [yellow]![/] Skipped vendor accounts, the registry in {Markup.Escape(accounts.Directory)} could not be used: {Markup.Escape(ex.Message)}");
        }
    }

    void Adopt(CodingAgentsStep.Options options, bool interactive, Func<string, bool> promptYesNo, Action<string> writeLine) {
        var vendors = new List<HarnessId>();
        if (!options.SkipClaude) vendors.Add(HarnessId.Claude);
        if (!options.SkipCodex) vendors.Add(HarnessId.Codex);

        foreach (var vendor in vendors) {
            var (envHome, wired) = vendor is HarnessId.Claude
                ? (env.Harnesses.Of<ClaudeHarness>().Paths.Home, env.Harnesses.Of<ClaudeHarness>().Signals.IsWired)
                : (env.Harnesses.Of<CodexHarness>().Paths.Home, env.Harnesses.Of<CodexHarness>().Signals.IsWired);

            if (wired) AccountAdoption.EnsureDefault(accounts, vendor, envHome, time);

            foreach (var account in AccountAdoption.Of(accounts, vendor).Where(a => !AccountDirectory.Same(a.Directory, envHome)))
                Report(account, Wire(account, options), writeLine);
        }

        foreach (var candidate in AccountDiscovery.Find(env.Home, accounts.Load(), Environment.GetEnvironmentVariable)
                                                   .Where(c => vendors.Contains(c.Vendor))) {
            var name = candidate.Vendor.ToString().ToLowerInvariant();
            var dir  = Markup.Escape(candidate.Directory);

            var hint = $"  Also found {dir} — add it with: kcap accounts add {name} {Markup.Escape(ShellArgument.Quote(candidate.Directory))}";

            if (options.NoPrompt || !interactive) {
                writeLine(hint);
                continue;
            }

            bool yes;
            try {
                yes = promptYesNo($"Record {candidate.Vendor} sessions from {dir} too?");
            } catch (InvalidOperationException) {
                writeLine(hint);
                continue;
            }

            if (!yes) continue;

            var account = AccountAdoption.EnsureDefault(accounts, candidate.Vendor, candidate.Directory, time);
            Report(account, Wire(account, options), writeLine);
        }
    }

    IReadOnlyList<WiringStep> Wire(VendorAccount account, CodingAgentsStep.Options options) {
        IReadOnlyCollection<string>? domains = options.SkipCodexNetworkAccess || env.CodexNetworkAllowDomains() is not { Count: > 0 } d ? null : d;
        var wiring = new WiringOptions(env.ResolvePluginPath(), env.Agents.UserSkillsDir, env.ResolveMcpBinaryPath, NetworkAllowDomains: domains);

        try {
            using var _ = accounts.Lock();

            return AccountWiring.Wire(account, env.Home, wiring);
        } catch (Exception ex) when (ex is TimeoutException or WaitHandleCannotBeOpenedException) {
            return [new WiringStep("registry lock", false, ex.Message)];
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return [new WiringStep("wiring", false, ex.Message)];
        }
    }

    static void Report(VendorAccount account, IReadOnlyList<WiringStep> steps, Action<string> writeLine) {
        var dir = Markup.Escape(account.Directory);

        if (AccountWiring.HasFatalFailure(steps)) {
            writeLine($"  [yellow]![/] Could not wire {dir}: {Markup.Escape(AccountWiring.FailureSummary(steps))}. Run [cyan]kcap accounts rewire[/] to retry.");
            return;
        }

        writeLine($"  [green]✓[/] {account.Vendor} account {dir} recorded");

        if (steps.Any(s => !s.Succeeded))
            writeLine($"    [yellow]![/] {Markup.Escape(AccountWiring.FailureSummary(steps))}");
    }
}
