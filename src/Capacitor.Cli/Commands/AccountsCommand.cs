using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Harness.Codex;

namespace Capacitor.Cli.Commands;

public sealed class AccountsCommand(AccountStore accounts, PluginEnvironment env, TimeProvider time) {
    const int    MinIdPrefix     = 4;
    const int    IdDisplayChars  = 8;
    const string SkipNetworkFlag = "--skip-codex-network-access";
    const string JsonFlag        = "--json";
    const string ListAction      = "list";
    const string UsageLine       = "Usage: kcap accounts [list|add|remove|rename|rewire|refresh]";

    bool _skipNetwork;
    bool _json;

    // Seam: a test removes the account here, where another command can.
    internal Action? BetweenWiringAndStamp { get; set; }

    // Seam: a test answers for the vendor CLI.
    internal Func<VendorAccount, IAccountProbe>? ProbeFor { get; set; }

    public async Task<int> HandleAsync(string[] args) {
        _skipNetwork = args.Contains(SkipNetworkFlag);
        _json        = args.Contains(JsonFlag);
        args         = [.. args.Where(a => a is not (SkipNetworkFlag or JsonFlag))];

        var action = args.Length < 2 ? ListAction : args[1];

        try {
            return args.Length < 2 ? await List() : args[1] switch {
                ListAction                      => await List(),
                "add" when args.Length == 4     => await Add(args[2], args[3]),
                "remove" when args.Length == 3  => await Remove(args[2]),
                "rename" when args.Length == 4  => await Rename(args[2], args[3]),
                "rewire" when args.Length <= 3  => await Rewire(args.Length == 3 ? args[2] : null),
                "refresh" when args.Length <= 3 => await Refresh(args.Length == 3 ? args[2] : null),
                _                               => await Usage(action),
            };
        } catch (RegistryUnavailable ex) {
            return await Refuse(action, $"Could not use the kcap account registry in {accounts.Directory}: {ex.Message}");
        }
    }

    sealed class RegistryUnavailable(string message) : Exception(message);

    static T Registry<T>(Func<T> access) {
        try {
            return access();
        } catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException
                                         or TimeoutException or WaitHandleCannotBeOpenedException) {
            throw new RegistryUnavailable(ex.Message);
        }
    }

    static async Task<T> RegistryAsync<T>(Func<Task<T>> access) {
        try {
            return await access();
        } catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException
                                         or TimeoutException or WaitHandleCannotBeOpenedException) {
            throw new RegistryUnavailable(ex.Message);
        }
    }

    static IReadOnlyList<WiringStep> Guarded(Func<IReadOnlyList<WiringStep>> wiring) {
        try {
            return wiring();
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return [new WiringStep("wiring", false, ex.Message)];
        }
    }

    WiringOptions Options => new(env.ResolvePluginPath(), env.Agents.UserSkillsDir, env.ResolveMcpBinaryPath,
                                 NetworkAllowDomains: _skipNetwork ? null : Domains(env.CodexNetworkAllowDomains()));

    static IReadOnlyCollection<string>? Domains(IReadOnlyList<string> domains) => domains.Count == 0 ? null : domains;

    async Task<int> List() {
        var registry = Registry(accounts.Load);
        foreach (var a in registry.Accounts)
            await Console.Out.WriteLineAsync($"{a.Vendor,-7} {a.Label,-20} {AccountStateLabels.For(a.Vendor, AccountWiring.State(a, env.Home)),-36} {a.Directory}  [{a.Id[..Math.Min(IdDisplayChars, a.Id.Length)]}]");

        var candidates = AccountDiscovery.Find(env.Home, registry, Environment.GetEnvironmentVariable);
        if (candidates.Count > 0) {
            await Console.Out.WriteLineAsync("");
            await Console.Out.WriteLineAsync("Found but not added:");
            foreach (var c in candidates)
                await Console.Out.WriteLineAsync($"  kcap accounts add {c.Vendor.ToString().ToLowerInvariant()} {ShellArgument.Quote(c.Directory)}   ({c.Reason})");
        }

        return 0;
    }

    async Task<int> Add(string vendorName, string directory) {
        if (!TryVendor(vendorName, out var vendor)) return await Usage("add");

        if (TryNormalize(directory) is not { } normalized || !Directory.Exists(normalized))
            return await Refuse("add", $"{directory} does not exist.");

        var account   = Registry(() => AccountAdoption.EnsureDefault(accounts, vendor, normalized, time));
        var wired     = await WireAndReport(account);
        var refreshed = await RefreshAndReport(account);
        if (!refreshed.Outcome.Found)
            return await Finish("add", account.Id, wired.Error is null, wired.Steps, wired.Revision, wired.Error);

        // The account is added whatever the vendor said, so a failed refresh only warns.
        foreach (var step in refreshed.Outcome.Steps.Where(s => !s.Succeeded))
            await Console.Error.WriteLineAsync($"Warning: could not refresh {refreshed.Label}: {step.Detail}");

        return await Finish("add", account.Id, wired.Error is null, [.. wired.Steps, .. refreshed.Outcome.Steps], refreshed.Outcome.Revision, wired.Error);
    }

    async Task<int> Remove(string idOrDir) {
        if (Resolve(idOrDir) is not { } account) return await NotFound("remove", idOrDir);

        IReadOnlyList<WiringStep> steps;
        using (Registry(accounts.Lock)) steps = Guarded(() => AccountWiring.Unwire(account, env.Home, accounts));
        if (AccountWiring.HasFatalFailure(steps)) {
            // A partial unwire changed the account on disk, so the failure is committed like a failed wiring.
            var summary   = AccountWiring.FailureSummary(steps);
            var error     = $"Could not unwire {account.Label}: {summary}. It stays registered.";
            var committed = StampWiring(account, summary).Revision;
            await Console.Error.WriteLineAsync(error);
            return await Finish("remove", account.Id, false, steps, committed, error);
        }

        string? leftover = null;
        var (_, revision) = Registry(() => accounts.MutateWithReadings(
            (r, _) => (r with { Accounts = [.. r.Accounts.Where(a => a.Id != account.Id)] }, 0),
            (_, held) => {
                try {
                    held.Delete(account.Id);
                } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                    leftover = ex.Message;
                }
            }));

        // A reading of an unregistered account is never written again, so a leftover file is only a warning.
        if (leftover is not null) await Console.Error.WriteLineAsync($"Warning: could not delete the usage reading of {account.Label}: {leftover}");
        await Say($"Removed {account.Vendor} account {account.Label}. {account.Directory} was left in place.");
        return await Finish("remove", account.Id, true, steps, revision);
    }

    async Task<int> Rename(string idOrDir, string label) {
        if (string.IsNullOrWhiteSpace(label)) return await Usage("rename");
        if (Resolve(idOrDir) is not { } account) return await NotFound("rename", idOrDir);

        var (_, revision) = Registry(() => accounts.MutateWithRevision(r => (r with { Accounts = [.. r.Accounts.Select(a => a.Id == account.Id ? a with { Label = label, LabelSetByUser = true } : a)] }, 0)));
        await Say($"Renamed to {label}.");
        return await Finish("rename", account.Id, true, [], revision);
    }

    async Task<int> Rewire(string? idOrDir) {
        IReadOnlyList<VendorAccount> targets;
        if (idOrDir is null) targets = Registry(accounts.Load).Accounts;
        else if (Resolve(idOrDir) is { } one) targets = [one];
        else return await NotFound("rewire", idOrDir);

        if (targets.Count == 0) {
            await Say("No accounts registered. Add one with: kcap accounts add <claude|codex> <dir>");
            return await Finish("rewire", null, true, [], null);
        }

        var   steps    = new List<WiringStep>();
        var   errors   = new List<string>();
        long? revision = null;
        foreach (var account in targets) {
            var wired = await WireAndReport(account);
            steps.AddRange(targets.Count == 1 ? wired.Steps : wired.Steps.Select(s => s with { Detail = $"{account.Label}: {s.Detail}" }));
            if (wired.Error is not null) errors.Add(wired.Error);
            revision = wired.Revision;
        }

        return await Finish("rewire", targets.Count == 1 ? targets[0].Id : null, errors.Count == 0, steps, revision,
            errors.Count == 0 ? null : string.Join(" ", errors));
    }

    async Task<int> Refresh(string? idOrDir) {
        IReadOnlyList<VendorAccount> targets;
        if (idOrDir is null) targets = Registry(accounts.Load).Accounts;
        else if (Resolve(idOrDir) is { } one) targets = [one];
        else return await NotFound("refresh", idOrDir);

        if (targets.Count == 0) {
            await Say("No accounts registered. Add one with: kcap accounts add <claude|codex> <dir>");
            return await Finish("refresh", null, true, [], null);
        }

        var   steps    = new List<WiringStep>();
        var   failed   = new List<string>();
        long? revision = null;
        foreach (var account in targets) {
            var (outcome, label) = await RefreshAndReport(account);
            if (!outcome.Found) {
                failed.Add($"Account {account.Label} was removed while it was being refreshed.");
                continue;
            }

            steps.AddRange(targets.Count == 1 ? outcome.Steps : outcome.Steps.Select(s => s with { Detail = $"{label}: {s.Detail}" }));
            failed.AddRange(outcome.Steps.Where(s => !s.Succeeded).Select(s => $"{label}: {s.Detail}"));
            revision = outcome.Revision;
        }

        // The text lines already carry each failure; --json keeps stdout to the one result line.
        if (_json)
            foreach (var failure in failed) await Console.Error.WriteLineAsync(failure);

        return await Finish("refresh", targets.Count == 1 ? targets[0].Id : null, failed.Count == 0, steps, revision,
            failed.Count == 0 ? null : string.Join(" ", failed));
    }

    /// <summary>Refreshes one account and prints its line. <c>Label</c> is the label the refresh left, which
    /// may now be the account's email.</summary>
    async Task<(RefreshOutcome Outcome, string Label)> RefreshAndReport(VendorAccount account) {
        var probe   = ProbeFor?.Invoke(account) ?? DefaultProbe(account);
        var outcome = await RegistryAsync(() => AccountRefresh.RunAsync(accounts, account.Id, probe, time, CancellationToken.None));
        if (!outcome.Found) return (outcome, account.Label);

        var current = Registry(accounts.Load).Accounts.FirstOrDefault(a => a.Id == account.Id) ?? account;
        var line    = $"{current.Label}: {SignInLabel(current.SignIn)}";
        if (current.Email is { } email && email != current.Label) line += $" ({email})";
        foreach (var step in outcome.Steps.Where(s => !s.Succeeded)) line += $"; {step.Name} failed: {step.Detail}";
        await Say(line);

        return (outcome, current.Label);
    }

    IAccountProbe DefaultProbe(VendorAccount account) =>
        account.Vendor is HarnessId.Codex
            ? new CodexAccountProbe(env.Harnesses, env.Home, time)
            : new ClaudeIdentityProbe(env.Harnesses, env.Home, time);

    static string SignInLabel(string? signIn) => signIn switch {
        SignInStates.SignedIn  => "signed in",
        SignInStates.SignedOut => "signed out",
        _                      => "sign-in unknown",
    };

    async Task<(IReadOnlyList<WiringStep> Steps, long Revision, string? Error)> WireAndReport(VendorAccount account) {
        IReadOnlyList<WiringStep> steps;
        using (Registry(accounts.Lock)) steps = Guarded(() => AccountWiring.Wire(account, env.Home, Options, accounts));
        BetweenWiringAndStamp?.Invoke();

        // The stamp's Mutate takes the lock held above, so it must come after that block. Committing after the
        // wiring is what lets a snapshot at this revision vouch for it.
        var fatal             = AccountWiring.HasFatalFailure(steps);
        var (revision, found) = StampWiring(account, fatal ? AccountWiring.FailureSummary(steps) : null);

        if (!found) {
            var removed = $"Account {account.Label} was removed while it was being wired.";
            await Console.Error.WriteLineAsync(removed);

            return (steps, revision, removed);
        }

        string? error = null;
        if (!fatal) {
            await Say($"Wired {account.Vendor} account {account.Label} ({account.Directory}).");
            if (account.Vendor is HarnessId.Codex)
                await Say($"  Trust the kcap hooks the next time you start Codex with CODEX_HOME={account.Directory}.");
        }

        if (steps.Any(s => !s.Succeeded)) {
            var message = fatal
                ? $"Could not wire {account.Label}: {AccountWiring.FailureSummary(steps)}. Run `kcap accounts rewire` to retry."
                : $"Warning: {account.Label}: {AccountWiring.FailureSummary(steps)}";
            await Console.Error.WriteLineAsync(message);
            if (fatal) error = message;
        }

        return (steps, revision, error);
    }

    // Rebuilds the list even when the stamp changes nothing, so the revision still moves for a rewire that found nothing to fix.
    // Found is false when a concurrent remove took the account between the wiring and this stamp.
    (long Revision, bool Found) StampWiring(VendorAccount account, string? failure) {
        var now = time.GetUtcNow();
        var (found, revision) = Registry(() => accounts.MutateWithRevision(r => (r with {
            Accounts = [.. r.Accounts.Select(a => a.Id != account.Id ? a
                                                : failure is null ? a with { WiredAt = now, WiringFailure = null }
                                                                  : a with { WiringFailure = failure })],
        }, r.Accounts.Any(a => a.Id == account.Id))));

        return (revision, found);
    }

    Task Say(string line) => _json ? Task.CompletedTask : Console.Out.WriteLineAsync(line);

    // list keeps its text listing under --json: its output is the data, not a verdict on an action.
    async Task<int> Finish(string action, string? accountId, bool succeeded, IReadOnlyList<WiringStep> steps, long? revision, string? error = null) {
        if (_json && action != ListAction) {
            var result = new AccountActionResultDto(action, accountId, succeeded, [.. steps.Select(s => new WiringStepDto(s.Name, s.Succeeded, s.Detail))], revision, error);
            await Console.Out.WriteLineAsync(JsonSerializer.Serialize(result, AccountActionResultJsonContext.Default.AccountActionResultDto));
        }

        return succeeded ? 0 : 1;
    }

    async Task<int> Refuse(string action, string error) {
        await Console.Error.WriteLineAsync(error);
        return await Finish(action, null, false, [], null, error);
    }

    VendorAccount? Resolve(string idOrDir) {
        var registry = Registry(accounts.Load);
        if (idOrDir.Length >= MinIdPrefix && registry.Accounts.FirstOrDefault(a => a.Id.StartsWith(idOrDir, StringComparison.OrdinalIgnoreCase)) is { } byId)
            return byId;

        return TryNormalize(idOrDir) is { } normalized
            ? registry.Accounts.FirstOrDefault(a => AccountDirectory.Same(a.Directory, normalized))
            : null;
    }

    string? TryNormalize(string path) {
        try {
            return AccountDirectory.Normalize(AccountDirectory.ExpandHome(path, env.Home));
        } catch (ArgumentException) {
            return null;
        } catch (NotSupportedException) {
            return null;
        } catch (IOException) {
            return null;
        }
    }

    static bool TryVendor(string name, out HarnessId vendor) {
        vendor = name.ToLowerInvariant() switch { "claude" => HarnessId.Claude, "codex" => HarnessId.Codex, _ => default };
        return name.ToLowerInvariant() is "claude" or "codex";
    }

    Task<int> NotFound(string action, string idOrDir) => Refuse(action, $"No account matches {idOrDir}. Run `kcap accounts` to list them.");

    async Task<int> Usage(string action) {
        await Console.Error.WriteLineAsync(EmbeddedResources.TryLoad("help-accounts.txt") ?? UsageLine);
        return await Finish(action, null, false, [], null, UsageLine);
    }
}
