using System.Text.Json;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.Core.Setup.Intelligence;
using Spectre.Console;

namespace Capacitor.Cli.Commands;

/// <summary><c>kcap local intelligence</c>: tells the local server who runs its background model calls — titles,
/// outcomes, evals — and whether imported sessions get outcomes. The local server authenticates nobody, so the answer
/// goes over the loopback lane with no credential.</summary>
sealed class LocalIntelligenceCommand(ICapacitorHttpClient http, LocalRoot root) {
    public const int Applied = 0;

    /// <summary>The server could not be asked, or may not have taken the answer.</summary>
    public const int Failed = 1;

    /// <summary>Nothing was stored: the arguments or the server refused the answer.</summary>
    public const int Refused = 2;

    internal Func<string?> Server { get; init; } = () => LocalEndpoints.Read(root).Active?.ServerUrl;

    internal Func<string, string?> Variable { get; init; } = Environment.GetEnvironmentVariable;

    public async Task<int> HandleAsync(string[] args) {
        var json = args.Contains(LocalIntelligenceArgs.JsonFlag);
        var (parsed, error) = LocalIntelligenceArgs.Parse(args[2..]);

        if (parsed is null) return Report(json, Refused, error!);

        if (Server() is not { } server)
            return Report(json, Failed, "There is no local installation to configure. Run kcap setup --local first.");

        string? key = null;

        if (parsed.Answer.Vendor is { } vendor) {
            var prompt = !json && AnsiConsole.Profile.Capabilities.Interactive ? () => AskKey(vendor) : (Func<string?>?)null;
            (key, var keyError) = IntelligenceKey.Resolve(vendor, Variable, prompt);

            if (key is null) return Report(json, Refused, keyError!);
        }

        var outcome = await IntelligenceSetupClient.ApplyAsync(
            http.Loopback(), server, parsed.Answer.Request(parsed.Model, key, parsed.ImportOutcomes), CancellationToken.None);

        return outcome switch {
            IntelligenceSetupOutcome.Applied { Response: var r } => Print(json, r),
            IntelligenceSetupOutcome.Refused { Error: var code } => Report(json, Refused, json ? code : IntelligenceRefusals.Describe(code)),
            IntelligenceSetupOutcome.Failed { Problem: var problem } => Report(json, Failed, $"Could not reach the local server: {problem}"),
            _ => Failed,
        };
    }

    internal static string AskKey(IntelligenceVendor vendor) =>
        AnsiConsole.Prompt(new TextPrompt<string>($"{Markup.Escape(vendor.Label)} API key:").Secret().AllowEmpty());

    static int Print(bool json, IntelligenceSetupResponse r) {
        if (json) {
            Write(IntelligenceCliLine.From(r));

            return Applied;
        }

        foreach (var line in Summary(r)) Console.Out.WriteLine($"  {line}");

        return Applied;
    }

    /// <summary>What the server now does, in the words setup uses too.</summary>
    internal static IEnumerable<string> Summary(IntelligenceSetupResponse r) {
        yield return r.Mode switch {
            IntelligenceModes.LocalAgent => $"Background intelligence: your local agent, {r.Harness}.",
            IntelligenceModes.Provider   => $"Background intelligence: {r.Vendor}, with your API key.",
            _                            => "Background intelligence: not set up. kcap local intelligence sets it up later.",
        };

        if (r.Mode == IntelligenceModes.None) yield break;

        yield return r.Resolving.Count == 0 ? "Nothing resolves a model yet." : $"Runs: {string.Join(", ", r.Resolving)}.";
        yield return r.ImportOutcomes ? "Imported sessions get an outcome." : "Imported sessions get no outcome.";
    }

    static int Report(bool json, int exit, string message) {
        if (json) Write(new IntelligenceCliLine(false, Error: message));
        else Console.Error.WriteLine($"  Error: {message}");

        return exit;
    }

    static void Write(IntelligenceCliLine line) {
        Console.Out.WriteLine(JsonSerializer.Serialize(line, IntelligenceCliJsonContext.Default.IntelligenceCliLine));
        Console.Out.Flush();
    }
}
