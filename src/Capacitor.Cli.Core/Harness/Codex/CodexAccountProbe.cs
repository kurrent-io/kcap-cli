using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Capacitor.Cli.Core.Accounts;

namespace Capacitor.Cli.Core.Harness.Codex;

/// <summary>Asks <c>codex app-server</c>, started under the account's <c>CODEX_HOME</c>, for the signed-in
/// account (<c>account/read</c>) and its usage (<c>account/rateLimits/read</c>). Codex makes the authenticated
/// call; kcap never reads Codex's auth files. The two steps fail independently. Afterwards it asks
/// <c>hooks/list</c> whether Codex trusts kcap's hooks, as a side answer that never fails either step.</summary>
public sealed class CodexAccountProbe(HarnessRegistry harnesses, UserHome home, TimeProvider time) : IAccountProbe {
    const int MethodNotFound = -32601;

    // Vendor text lands in the published refresh and limits errors.
    const int PreviewChars = 200;

    static readonly TimeSpan ExitGrace = TimeSpan.FromSeconds(2);

    internal TimeSpan Deadline { get; init; } = TimeSpan.FromSeconds(10);

    public async Task<ProbeResult> ProbeAsync(VendorAccount account, CancellationToken ct) {
        if (harnesses.ResolveLaunchable(HarnessId.Codex) is not { } exe) return Failed("codex not found on PATH or in the desktop app");

        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var psi  = new ProcessStartInfo {
            FileName               = exe,
            WorkingDirectory       = Path.GetTempPath(),
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            StandardInputEncoding  = utf8,
            StandardOutputEncoding = utf8,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        psi.ArgumentList.Add("app-server");
        VendorProcess.PlanHome(psi, HarnessId.Codex, account.Directory, home);

        var paths             = AccountLayouts.Codex(home, account.Directory);
        var fingerprintBefore = CodexTrustFingerprint.Of(paths);

        string? startError = null;
        using var process = CliProcess.TryStart(psi, message => startError = message);
        if (process is null) return Failed(startError ?? "could not start codex app-server");

        string? lastStderr = null;
        process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) lastStderr = e.Data; };
        process.BeginErrorReadLine();

        using var deadline = new CancellationTokenSource(Deadline, time);
        using var linked   = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        var rpc = new Rpc(process.StandardInput, process.StandardOutput, linked.Token);

        var            identityDone  = false;
        string?        identityError = null;
        string?        signIn        = null;
        JsonElement?   chatgpt       = null;
        string?        routingId     = null;
        var            limitsDone    = false;
        string?        limitsError   = null;
        string?        limitsNote    = null;
        JsonElement?   limits        = null;
        DateTimeOffset limitsAt      = default;
        HookTrustObservation? hookTrust = null;

        try {
            await rpc.RequestAsync("initialize", w => {
                w.WriteStartObject("clientInfo");
                w.WriteString("name", "kcap");
                w.WriteString("version", CapacitorVersion.Current());
                w.WriteEndObject();
            }).ConfigureAwait(false);

            try {
                (signIn, chatgpt, routingId) = ReadAccount(await rpc.RequestAsync("account/read", _ => { }).ConfigureAwait(false));
            } catch (RpcError ex) {
                identityError = $"account/read: {ex.Message}";
            }
            identityDone = true;

            // Only a ChatGPT login has usage limits; asking without one fails every time.
            limitsNote = identityError is not null || chatgpt is not null ? null
                       : signIn == SignInStates.SignedOut                ? "not signed in"
                                                                         : "no ChatGPT account";
            if (limitsNote is null) {
                try {
                    limits   = await rpc.RequestAsync("account/rateLimits/read", w => w.WriteBoolean("excludeResetCreditDetails", true)).ConfigureAwait(false);
                    limitsAt = time.GetUtcNow();
                } catch (RpcError ex) {
                    limitsError = $"account/rateLimits/read: {ex.Message}";
                }
            }
            limitsDone = true;

            hookTrust = await ReadHookTrustAsync(rpc, psi.WorkingDirectory, paths, fingerprintBefore).ConfigureAwait(false);
        } catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
            var timeout = $"codex app-server did not answer within {Deadline.TotalSeconds:0.#}s";
            if (!identityDone) identityError = timeout;
            if (!limitsDone) limitsError = timeout;
        } catch (Exception ex) when (ex is RpcError or IOException or ObjectDisposedException) {
            var failure = ex is RpcError ? $"codex app-server initialize: {ex.Message}" : $"codex app-server stopped: {Preview(lastStderr ?? ex.Message)}";
            if (!identityDone) identityError = failure;
            if (!limitsDone) limitsError = failure;
        } finally {
            await ShutDownAsync(process).ConfigureAwait(false);
        }

        ct.ThrowIfCancellationRequested();

        var usageId     = limits is { } read ? CodexRateLimits.AccountIdFromRateLimitsRead(read) : null;
        var observation = limits is { } result ? CodexRateLimits.FromRateLimitsRead(result, limitsAt) : null;

        // The usage read can answer for another identity than the signed-in one; its numbers are not this login's.
        if (routingId is not null && usageId is not null && routingId != usageId) {
            observation = null;
            limitsError = "usage identity does not match the signed-in account";
        }

        var identity = identityError is null && chatgpt is { } a
            ? new AccountIdentity(a.Str("email"), null, null, a.Str("planType"), routingId ?? usageId)
            : null;

        return new ProbeResult(identityError is null ? signIn : null, identity, identityError, observation, limitsError, hookTrust, limitsNote);
    }

    /// <summary>Null when Codex gives no usable answer or either Codex file changed or was unreadable around it:
    /// a verdict that cannot be tied to the bytes it was given for is no verdict.</summary>
    static async Task<HookTrustObservation?> ReadHookTrustAsync(Rpc rpc, string cwd, CodexPaths paths, string? before) {
        if (before is null) return null;

        JsonElement result;
        try {
            result = await rpc.RequestAsync("hooks/list", w => {
                w.WriteStartArray("cwds");
                w.WriteStringValue(cwd);
                w.WriteEndArray();
            }).ConfigureAwait(false);
        } catch (RpcError) {
            return null;
        }

        if (result.ValueKind != JsonValueKind.Object || result.Arr("data") is null) return null;

        var after = CodexTrustFingerprint.Of(paths);
        if (after is null || after != before) return null;

        var trust = CodexHookTrust.Classify(CodexHookTrust.ParseHooksList(result)) is CodexHookTrustDecision.Proceed
            ? HookTrustStates.Trusted
            : HookTrustStates.Untrusted;
        return new HookTrustObservation(trust, after);
    }

    /// <summary>The sign-in, the account object when it is a ChatGPT login (the only kind that names one), and
    /// the stable account id <c>workspaceRouting</c> carries.</summary>
    static (string? SignIn, JsonElement? ChatGpt, string? AccountId) ReadAccount(JsonElement result) {
        var accountId = result.Obj("workspaceRouting")?.Str("chatgptAccountId");
        if (result.Obj("account") is { } account)
            return (SignInStates.SignedIn, account.Str("type") == "chatgpt" ? account : null, accountId);

        return (result.Bool("requiresOpenaiAuth") == true ? SignInStates.SignedOut : null, null, null);
    }

    async Task ShutDownAsync(Process process) {
        try {
            process.StandardInput.Close();
            using var grace = new CancellationTokenSource(ExitGrace, time);
            await process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
        } catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidOperationException) {
            // Still running, or its input already gone: the kill below settles it.
        }

        VendorProcess.Stop(process);
    }

    static ProbeResult Failed(string error) => new(null, null, error, null, error);

    static string Preview(string text) {
        var trimmed = text.Trim();
        return trimmed.Length > PreviewChars ? trimmed[..PreviewChars] : trimmed;
    }

    sealed class RpcError(string message) : Exception(message);

    /// <summary>Newline-delimited JSON-RPC, one request in flight at a time. A request from the server is
    /// refused with method-not-found: this client offers nothing, and an unanswered request could stall the
    /// answer it waits for.</summary>
    sealed class Rpc(StreamWriter input, StreamReader output, CancellationToken ct) {
        long _nextId;

        public async Task<JsonElement> RequestAsync(string method, Action<Utf8JsonWriter> writeParams) {
            var id = ++_nextId;
            await SendAsync(w => {
                w.WriteNumber("id", id);
                w.WriteString("method", method);
                w.WriteStartObject("params");
                writeParams(w);
                w.WriteEndObject();
            }).ConfigureAwait(false);

            while (true) {
                var line = await output.ReadLineAsync(ct).AsTask().WaitAsync(ct).ConfigureAwait(false)
                        ?? throw new IOException("codex app-server closed its output");

                JsonElement frame;
                try {
                    using var doc = JsonDocument.Parse(line);
                    frame = doc.RootElement.Clone();
                } catch (JsonException) {
                    continue;
                }

                if (!frame.IsObject) continue;

                if (frame.Str("method") is { } serverMethod) {
                    if (frame.Prop("id") is { } requestId) await RefuseAsync(requestId, serverMethod).ConfigureAwait(false);
                    continue;
                }

                if (frame.Num("id") != id) continue;

                if (frame.Obj("error") is { } error)
                    throw new RpcError(error.Str("message") is { } message ? $"{Preview(message)} ({error.Num("code")})" : $"error {error.Num("code")}");

                return frame.Prop("result") ?? default;
            }
        }

        Task RefuseAsync(JsonElement id, string method) =>
            SendAsync(w => {
                w.WritePropertyName("id");
                id.WriteTo(w);
                w.WriteStartObject("error");
                w.WriteNumber("code", MethodNotFound);
                w.WriteString("message", $"Method not found: {method}");
                w.WriteEndObject();
            });

        async Task SendAsync(Action<Utf8JsonWriter> body) {
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer)) {
                writer.WriteStartObject();
                writer.WriteString("jsonrpc", "2.0");
                body(writer);
                writer.WriteEndObject();
            }

            await input.WriteAsync((Encoding.UTF8.GetString(buffer.ToArray()) + "\n").AsMemory(), ct).ConfigureAwait(false);
            await input.FlushAsync(ct).ConfigureAwait(false);
        }
    }
}
