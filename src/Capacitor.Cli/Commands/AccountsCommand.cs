using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli.Commands;

public sealed class AccountsCommand(AccountStore accounts, PluginEnvironment env, TimeProvider time) {
    const int MinIdPrefix = 4;

    // A failed sandbox-network step is a warning, as it is for `kcap plugin`.
    const string NetworkStep = "network";

    public async Task<int> HandleAsync(string[] args) {
        try {
            return args.Length < 2 ? await List() : args[1] switch {
                "list"                         => await List(),
                "add" when args.Length == 4    => await Add(args[2], args[3]),
                "remove" when args.Length == 3 => await Remove(args[2]),
                "rename" when args.Length == 4 => await Rename(args[2], args[3]),
                "rewire" when args.Length <= 3 => await Rewire(args.Length == 3 ? args[2] : null),
                _                              => await Usage(),
            };
        } catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException
                                         or TimeoutException or WaitHandleCannotBeOpenedException) {
            await Console.Error.WriteLineAsync($"Could not use the kcap account registry in {accounts.Directory}: {ex.Message}");
            return 1;
        }
    }

    WiringOptions Options => new(env.ResolvePluginPath(), env.Agents.UserSkillsDir, env.ResolveMcpBinaryPath, NetworkAllowDomains: null);

    async Task<int> List() {
        var registry = accounts.Load();
        foreach (var a in registry.Accounts)
            await Console.Out.WriteLineAsync($"{a.Vendor,-7} {a.Label,-20} {StateLabel(a.Vendor, AccountWiring.State(a, env.Home)),-36} {a.Directory}  [{a.Id[..8]}]");

        var candidates = AccountDiscovery.Find(env.Home, registry, Environment.GetEnvironmentVariable);
        if (candidates.Count > 0) {
            await Console.Out.WriteLineAsync("");
            await Console.Out.WriteLineAsync("Found but not added:");
            foreach (var c in candidates)
                await Console.Out.WriteLineAsync($"  kcap accounts add {c.Vendor.ToString().ToLowerInvariant()} {c.Directory}   ({c.Reason})");
        }

        return 0;
    }

    async Task<int> Add(string vendorName, string directory) {
        if (!TryVendor(vendorName, out var vendor)) return await Usage();

        if (TryNormalize(directory) is not { } normalized || !Directory.Exists(normalized)) {
            await Console.Error.WriteLineAsync($"{directory} does not exist.");
            return 1;
        }

        var account = AccountAdoption.EnsureDefault(accounts, vendor, normalized, time);
        return await WireAndReport(account) ? 0 : 1;
    }

    async Task<int> Remove(string idOrDir) {
        if (Resolve(idOrDir) is not { } account) return await NotFound(idOrDir);

        var steps = AccountWiring.Unwire(account, env.Home);
        if (!IsFatal(steps)) {
            accounts.Mutate(r => (r with { Accounts = [.. r.Accounts.Where(a => a.Id != account.Id)] }, 0));
            await Console.Out.WriteLineAsync($"Removed {account.Vendor} account {account.Label}. {account.Directory} was left in place.");
            return 0;
        }

        await Console.Error.WriteLineAsync($"Could not unwire {account.Label}: {Failures(steps)}. It stays registered.");
        return 1;
    }

    async Task<int> Rename(string idOrDir, string label) {
        if (string.IsNullOrWhiteSpace(label)) return await Usage();
        if (Resolve(idOrDir) is not { } account) return await NotFound(idOrDir);

        accounts.Mutate(r => (r with { Accounts = [.. r.Accounts.Select(a => a.Id == account.Id ? a with { Label = label } : a)] }, 0));
        await Console.Out.WriteLineAsync($"Renamed to {label}.");
        return 0;
    }

    async Task<int> Rewire(string? idOrDir) {
        IReadOnlyList<VendorAccount> targets;
        if (idOrDir is null) targets = accounts.Load().Accounts;
        else if (Resolve(idOrDir) is { } one) targets = [one];
        else return await NotFound(idOrDir);

        var ok = true;
        foreach (var account in targets) ok &= await WireAndReport(account);
        return ok ? 0 : 1;
    }

    async Task<bool> WireAndReport(VendorAccount account) {
        IReadOnlyList<WiringStep> steps;
        using (accounts.Lock()) steps = AccountWiring.Wire(account, env.Home, Options);

        var fatal = IsFatal(steps);
        if (!fatal) {
            await Console.Out.WriteLineAsync($"Wired {account.Vendor} account {account.Label} ({account.Directory}).");
            if (account.Vendor is HarnessId.Codex)
                await Console.Out.WriteLineAsync($"  Trust the kcap hooks the next time you start Codex with CODEX_HOME={account.Directory}.");
        }

        if (steps.Any(s => !s.Succeeded)) {
            await Console.Error.WriteLineAsync(fatal
                ? $"Could not wire {account.Label}: {Failures(steps)}. Run `kcap accounts rewire` to retry."
                : $"Warning: {account.Label}: {Failures(steps)}");
        }

        return !fatal;
    }

    VendorAccount? Resolve(string idOrDir) {
        var registry = accounts.Load();
        if (idOrDir.Length >= MinIdPrefix && registry.Accounts.FirstOrDefault(a => a.Id.StartsWith(idOrDir, StringComparison.OrdinalIgnoreCase)) is { } byId)
            return byId;

        var path = idOrDir == "~" ? env.Home.Path
                 : idOrDir.StartsWith("~/", StringComparison.Ordinal) ? Path.Combine(env.Home.Path, idOrDir[2..])
                 : idOrDir;

        return TryNormalize(path) is { } normalized
            ? registry.Accounts.FirstOrDefault(a => AccountDirectory.Same(a.Directory, normalized))
            : null;
    }

    string? TryNormalize(string path) {
        try {
            var expanded = path == "~" ? env.Home.Path
                         : path.StartsWith("~/", StringComparison.Ordinal) ? Path.Combine(env.Home.Path, path[2..])
                         : path;

            return AccountDirectory.Normalize(expanded);
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

    static string StateLabel(HarnessId vendor, RecordingState state) => state switch {
        RecordingState.Recording                              => "recording",
        RecordingState.Installed when vendor is HarnessId.Codex => "hooks installed (trust in Codex)",
        RecordingState.Installed                              => "wired (starts on next launch)",
        RecordingState.Broken                                 => "broken — run kcap accounts rewire",
        _                                                     => "not wired",
    };

    static bool IsFatal(IReadOnlyList<WiringStep> steps) => steps.Any(s => !s.Succeeded && s.Name != NetworkStep);

    static string Failures(IReadOnlyList<WiringStep> steps) =>
        string.Join(", ", steps.Where(s => !s.Succeeded).Select(s => $"{s.Name}: {s.Detail}"));

    static async Task<int> NotFound(string idOrDir) {
        await Console.Error.WriteLineAsync($"No account matches {idOrDir}. Run `kcap accounts` to list them.");
        return 1;
    }

    static async Task<int> Usage() {
        await Console.Error.WriteLineAsync(EmbeddedResources.TryLoad("help-accounts.txt") ?? "Usage: kcap accounts [list|add|remove|rename|rewire]");
        return 1;
    }
}
