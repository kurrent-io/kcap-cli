using System.Diagnostics;
using System.Text.Json;
using Capacitor.Cli.Core.Accounts;

namespace Capacitor.Cli.Core.Harness.Claude;

/// <summary>Asks <c>claude auth status --json</c> who is signed in to an account's directory. It prints no
/// secret, and Claude readings come from the status line, so this probe reports no limits.</summary>
public sealed class ClaudeIdentityProbe(HarnessRegistry harnesses, UserHome home, TimeProvider time) : IAccountProbe {
    const int PreviewChars = 200;

    internal TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);

    public async Task<ProbeResult> ProbeAsync(VendorAccount account, CancellationToken ct) {
        if (harnesses.ResolveLaunchable(HarnessId.Claude) is not { } exe) return Failed("claude not found on PATH or in the desktop app");

        var psi = new ProcessStartInfo {
            FileName               = exe,
            WorkingDirectory       = Path.GetTempPath(),
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            RedirectStandardInput  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        psi.ArgumentList.Add("auth");
        psi.ArgumentList.Add("status");
        psi.ArgumentList.Add("--json");
        VendorProcess.PlanHome(psi, HarnessId.Claude, account.Directory, home);

        string? startError = null;
        using var process = CliProcess.TryStart(psi, message => startError = message);
        if (process is null) return Failed(startError ?? "could not start claude");

        process.StandardInput.Close();

        using var deadline = new CancellationTokenSource(Timeout, time);
        using var linked   = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        string stdout, stderr;
        try {
            var reading = process.StandardOutput.ReadToEndAsync(linked.Token);
            var errors  = process.StandardError.ReadToEndAsync(linked.Token);
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            stdout = await reading.ConfigureAwait(false);
            stderr = await errors.ConfigureAwait(false);
        } catch (OperationCanceledException) {
            VendorProcess.Stop(process);
            ct.ThrowIfCancellationRequested();
            return Failed($"claude auth status did not answer within {Timeout.TotalSeconds:0}s");
        }

        // A signed-out answer may come with a non-zero exit, so the JSON decides, not the exit code.
        return Parse(stdout) ?? Failed($"claude auth status exited {process.ExitCode}: {Preview(stderr.Length > 0 ? stderr : stdout)}");
    }

    static ProbeResult? Parse(string stdout) {
        try {
            using var doc  = JsonDocument.Parse(stdout);
            var       root = doc.RootElement;
            return root.Bool("loggedIn") switch {
                true  => new(SignInStates.SignedIn,
                             new AccountIdentity(root.Str("email"), root.Str("orgId"), root.Str("orgName"), root.Str("subscriptionType"), null),
                             null, null, null),
                false => new(SignInStates.SignedOut, null, null, null, null),
                null  => null,
            };
        } catch (JsonException) {
            return null;
        }
    }

    static ProbeResult Failed(string error) => new(null, null, error, null, null);

    static string Preview(string text) {
        var trimmed = text.Trim();
        return trimmed.Length > PreviewChars ? trimmed[..PreviewChars] : trimmed;
    }
}
