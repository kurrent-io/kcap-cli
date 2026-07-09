using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// Localhost-only HTTP bridge that fronts the server's permission flow for spawned
/// Claude processes. The daemon's local Claude permission hook posts here instead of
/// going through the server's <c>/hooks/permission-request</c> route — that route runs
/// through Cloudflare which severs the long-poll at ~120s; the bridge invokes the
/// server's SignalR <c>RequestPermission</c> hub method over the daemon's persistent
/// connection, where no HTTP-request timeout applies.
///
/// Bound to <c>127.0.0.1</c> on a random ephemeral port. The orchestrator publishes
/// <see cref="BaseUrl"/> via the <c>KCAP_DAEMON_URL</c> env var on every spawned
/// agent so the CLI <c>permission-request</c> command can detect and use it.
/// </summary>
internal sealed partial class LocalPermissionBridge(
        ServerConnection               server,
        ILogger<LocalPermissionBridge> logger
    ) : IHostedService, IAsyncDisposable {
    const int    MaxBindAttempts = 8;
    const string PathSuffix      = "/permission-request";

    HttpListener?            _listener;
    Task?                    _acceptLoop;
    CancellationTokenSource? _cts;
    string?                  _sharedToken;
    int                      _port;

    // AI-1292: live per-reviewer tokens → each token's bound (read-only) kcap allowlist servers.
    // A request arriving on a reviewer token auto-approves that reviewer's kcap tools (no human is
    // present); the shared token keeps the interactive prompt path. The reviewer token is a secret
    // only the reviewer process receives (in its own KCAP_DAEMON_URL), so an interactive agent —
    // which holds only the shared token — cannot address the unattended path.
    readonly ConcurrentDictionary<string, string[]> _reviewerTokens = new(StringComparer.Ordinal);
    readonly object                                 _prefixLock     = new();

    /// <summary>
    /// Full URL the spawned CLI hook command should POST to. Includes the random per-run
    /// token as a path segment so unrelated local processes can't pose as a Claude hook
    /// even if they discover the ephemeral port. This is the SHARED (interactive) token; a
    /// review-flow reviewer instead gets a dedicated token from <see cref="RegisterReviewerToken"/>.
    /// </summary>
    public string? BaseUrl { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken) {
        // The TcpListener-based port probe has a TOCTOU window before HttpListener.Start
        // binds the same port. Retry up to MaxBindAttempts on TRANSIENT bind failures so a
        // single rare race doesn't crash daemon startup. Non-transient errors (URLACL on
        // Windows, permission issues) bubble up immediately so they aren't masked.
        for (var attempt = 1; attempt <= MaxBindAttempts; attempt++) {
            var port  = ReserveFreeLoopbackPort();
            var token = NewToken();

            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/{token}/");

            try {
                listener.Start();
                _listener    = listener;
                _sharedToken = token;
                _port        = port;
                BaseUrl      = $"http://127.0.0.1:{port}/{token}";

                break;
            } catch (HttpListenerException ex) when (attempt < MaxBindAttempts && IsAddressInUse(ex)) {
                LogBindRetry(logger, attempt, port, ex.Message);
                listener.Close();
            }
        }

        if (_listener is null)
            throw new InvalidOperationException($"Failed to bind LocalPermissionBridge after {MaxBindAttempts} attempts");

        _cts        = new();
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token), _cts.Token);
        LogBridgeStarted(logger, BaseUrl!);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Detects "address already in use" across platforms. HttpListenerException's ErrorCode
    /// is the underlying socket/Win32 error: 10048 = WSAEADDRINUSE (Windows), 48 = EADDRINUSE
    /// (macOS), 98 = EADDRINUSE (Linux). Anything else (URLACL denial code 5, etc.) is not
    /// transient and shouldn't be retried.
    /// </summary>
    static bool IsAddressInUse(HttpListenerException ex) =>
        ex.ErrorCode is 10048 or 48 or 98;

    public async Task StopAsync(CancellationToken cancellationToken) {
        if (_cts is not null) await _cts.CancelAsync();
        _listener?.Stop();

        if (_acceptLoop is not null) {
            try {
                await _acceptLoop.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
            } catch {
                /* shutting down */
            }
        }
    }

    public async ValueTask DisposeAsync() {
        await StopAsync(CancellationToken.None);
        _listener?.Close();
        _cts?.Dispose();
    }

    /// <summary>
    /// AI-1292: mint a dedicated bridge token for an unattended review-flow reviewer, bound to the
    /// read-only kcap servers it may auto-approve (<paramref name="allowlistServers"/>, canonical
    /// ids). Returns the full URL the reviewer must use as its <c>KCAP_DAEMON_URL</c>. The token is
    /// a CSPRNG secret and gets its own listener prefix so only that reviewer's hook can reach the
    /// unattended path. Revoke with <see cref="RevokeReviewerToken"/> once the reviewer exits.
    /// </summary>
    public string RegisterReviewerToken(IReadOnlyList<string> allowlistServers) {
        if (_listener is null || _sharedToken is null)
            throw new InvalidOperationException("LocalPermissionBridge not started");

        lock (_prefixLock) {
            var token = NewToken();
            while (string.Equals(token, _sharedToken, StringComparison.Ordinal) || _reviewerTokens.ContainsKey(token))
                token = NewToken();   // CSPRNG collisions are negligible; never silently reuse one

            _listener.Prefixes.Add($"http://127.0.0.1:{_port}/{token}/");
            _reviewerTokens[token] = [.. allowlistServers];

            return $"http://127.0.0.1:{_port}/{token}";
        }
    }

    /// <summary>Revoke a reviewer token (accepts the URL from <see cref="RegisterReviewerToken"/> or
    /// the bare token). Idempotent. After revocation, requests on that token 404 (fail-safe).</summary>
    public void RevokeReviewerToken(string reviewerBridgeUrlOrToken) {
        var token = ExtractToken(reviewerBridgeUrlOrToken);
        if (token is null) return;

        lock (_prefixLock) {
            if (_reviewerTokens.TryRemove(token, out _))
                _listener?.Prefixes.Remove($"http://127.0.0.1:{_port}/{token}/");
        }
    }

    // 128 bits of CSPRNG entropy as 32 lowercase hex chars — same shape as the original shared
    // token, unguessable, and safe to place in a bearer URL.
    static string NewToken() => RandomNumberGenerator.GetHexString(32, lowercase: true);

    // Accept either the full reviewer URL (http://127.0.0.1:{port}/{token}) or a bare token.
    static string? ExtractToken(string urlOrToken) {
        if (string.IsNullOrWhiteSpace(urlOrToken)) return null;

        return Uri.TryCreate(urlOrToken, UriKind.Absolute, out var uri)
            ? uri.AbsolutePath.Trim('/')
            : urlOrToken.Trim('/');
    }

    static int ReserveFreeLoopbackPort() {
        // HttpListener doesn't accept port 0 in its prefix; reserve a free ephemeral
        // port via TcpListener and immediately release. There's a TOCTOU window before
        // HttpListener.Start binds the same port, but on a single-user developer machine
        // the race is benign — port collisions are vanishingly rare.
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        try { return ((IPEndPoint)probe.LocalEndpoint).Port; } finally { probe.Stop(); }
    }

    async Task AcceptLoopAsync(CancellationToken ct) {
        while (!ct.IsCancellationRequested && _listener!.IsListening) {
            HttpListenerContext context;

            try {
                context = await _listener.GetContextAsync();
            } catch (ObjectDisposedException) {
                break;
            } catch (HttpListenerException) {
                break;
            }

            // Fire-and-forget — each request is independent and the SignalR
            // round-trip blocks until the user decides (potentially hours).
            _ = Task.Run(() => HandleAsync(context, ct), ct);
        }
    }

    async Task HandleAsync(HttpListenerContext context, CancellationToken ct) {
        try {
            // Require token + vendor + endpoint match. The HttpListener prefix already routed us
            // here, but we re-validate explicitly so a stray prefix can't quietly admit anything.
            // Path shape: /{token}/{vendor}/permission-request.
            var path = context.Request.Url?.AbsolutePath;

            if (path is null
             || !path.EndsWith(PathSuffix, StringComparison.Ordinal)
             || context.Request.HttpMethod != "POST") {
                context.Response.StatusCode = 404;
                context.Response.Close();

                return;
            }

            // Extract the token (first path segment) and classify it against the LIVE token set:
            // the shared token → interactive; a live reviewer token → unattended auto-approve. An
            // unknown or revoked token fails safe with a 404.
            var trimmed    = path.TrimStart('/');
            var firstSlash = trimmed.IndexOf('/');

            if (firstSlash <= 0) {
                context.Response.StatusCode = 404;
                context.Response.Close();

                return;
            }

            var token      = trimmed[..firstSlash];
            var isShared   = string.Equals(token, _sharedToken, StringComparison.Ordinal);
            var isReviewer = _reviewerTokens.TryGetValue(token, out var reviewerAllowlist);

            if (!isShared && !isReviewer) {
                context.Response.StatusCode = 404;
                context.Response.Close();

                return;
            }

            // Vendor is the segment between "/{token}/" and the "/permission-request" suffix.
            var afterToken = path[(token.Length + 2)..];
            var vendor     = afterToken.Length > PathSuffix.Length ? afterToken[..^PathSuffix.Length] : "";

            if (vendor is not ("claude" or "codex")) {
                context.Response.StatusCode = 404;
                context.Response.Close();

                return;
            }

            using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
            var       body   = await reader.ReadToEndAsync(ct);

            JsonNode? node;

            try {
                node = JsonNode.Parse(body);
            } catch (JsonException) {
                // Malformed JSON from the local hook caller — that's a 400 (caller error),
                // not a 500 (daemon failure). Without this branch the outer Exception catch
                // would mislabel client-side parse errors as server faults.
                context.Response.StatusCode = 400;
                context.Response.Close();

                return;
            }

            if (node is null) {
                context.Response.StatusCode = 400;
                context.Response.Close();

                return;
            }

            // Match the wire shape Claude's PermissionRequest hook posts: session_id is the
            // canonical (dashless) form, tool_name + tool_input + permission_suggestions are
            // pass-through.
            var sessionId = node["session_id"]?.GetValue<string>()?.Replace("-", "");

            if (sessionId is null) {
                context.Response.StatusCode = 400;
                context.Response.Close();

                return;
            }

            var toolName    = node["tool_name"]?.GetValue<string>();
            var toolInput   = ExtractElement(node, "tool_input");
            var suggestions = ExtractElement(node, "permission_suggestions");

            // The HttpListener API doesn't expose a per-request "client disconnected" token,
            // so the SignalR call is bound to the daemon-shutdown token only. RequestPermissionAsync
            // now retries across reconnects, so if Claude exits mid-wait the server hub call can
            // stay open across reconnects until the user decides or the daemon shuts down — it is
            // NOT bounded by a single connection's lifetime (the hook client's ~10h timeout is the
            // practical end-to-end ceiling). Switching to Kestrel + HttpContext.RequestAborted
            // would give us per-request cancellation; out of scope for this PR.
            PermissionDecision decision;

            if (IsFlowResultSubmission(toolName)) {
                // AI-1139: the reviewer's own result-submission tool (kcap-flow-result →
                // submit_review_result) is unique to a server only injected for review-flow
                // reviewers, so it's always safe. Auto-approve on ANY live token without a server
                // round-trip — the unattended reviewer can never get a user decision otherwise.
                LogFlowResultAutoApproved(logger, sessionId, vendor);
                decision = new PermissionDecision("allow", null, null);
            } else if (isReviewer) {
                // AI-1292: an unattended review-flow reviewer. Its Codex MCP config confines it to
                // the bound (read-only) kcap servers, so a tool call arriving on its token is an
                // auto-approvable kcap tool. A reviewer request MUST carry a tool to classify; an
                // out-of-allowlist server-qualified call is an authorization failure we DENY
                // outright — never deferred to a prompt no human can answer (that would hang).
                if (string.IsNullOrEmpty(toolName)) {
                    context.Response.StatusCode = 400;
                    context.Response.Close();

                    return;
                }

                if (IsReviewerToolAllowed(toolName, reviewerAllowlist!)) {
                    decision = new PermissionDecision("allow", null, null);
                } else {
                    LogReviewerToolDenied(logger, sessionId, toolName);
                    decision = new PermissionDecision("deny", null, null);
                }
            } else {
                // Shared (interactive) token → the server permission path, unchanged.
                try {
                    decision = await server.RequestPermissionAsync(sessionId, toolName, toolInput, suggestions, ct);
                } catch (Exception ex) {
                    LogRequestPermissionFailed(logger, ex, sessionId);
                    decision = new PermissionDecision("deny", null, null);
                }
            }

            var responseJson = BuildHookResponseJson(decision, vendor);
            var bytes        = Encoding.UTF8.GetBytes(responseJson);

            context.Response.ContentType     = "application/json";
            context.Response.StatusCode      = 200;
            context.Response.ContentLength64 = bytes.LongLength;
            await context.Response.OutputStream.WriteAsync(bytes, ct);
            context.Response.Close();
        } catch (Exception ex) {
            LogBridgeHandlerError(logger, ex);

            try {
                context.Response.StatusCode = 500;
                context.Response.Close();
            } catch {
                /* response already closed */
            }
        }
    }

    static JsonElement? ExtractElement(JsonNode root, string property) {
        var child = root[property];

        if (child is null) return null;

        // JsonNode → JsonElement via raw JSON is the AOT-safe path; child.GetValue<JsonElement>()
        // is finicky on JsonObject children. Dispose the document — Clone() copies the buffer
        // so the returned element stays valid.
        using var doc = JsonDocument.Parse(child.ToJsonString());

        return doc.RootElement.Clone();
    }

    /// <summary>
    /// True when the permission request is for the review-flow reviewer's result-submission tool
    /// (the <c>kcap-flow-result</c> server's <c>submit_review_result</c>). This auto-approve bypasses
    /// the server permission boundary, so the match is deliberately precise rather than a loose
    /// substring: it accepts either the bare tool name (a vendor that passes the raw MCP tool name,
    /// e.g. Codex) OR a vendor-prefixed id that both names the <c>kcap-flow-result</c> server AND ends
    /// in the exact tool — e.g. Claude's <c>mcp__kcap_flow_result__submit_review_result</c> (Claude
    /// sanitizes the hyphens to underscores). Requiring the server token means a coincidental
    /// "…submit_review_result" exposed by some other MCP server on an interactive hosted agent can't
    /// slip past the prompt.
    /// </summary>
    static bool IsFlowResultSubmission(string? toolName) {
        if (string.IsNullOrEmpty(toolName)) return false;

        // Bare tool name, no server prefix.
        if (string.Equals(toolName, "submit_review_result", StringComparison.Ordinal)) return true;

        // Vendor-prefixed MCP id: require the flow-result server identity AND the exact tool suffix.
        var namesFlowResultServer =
            toolName.Contains("kcap_flow_result", StringComparison.Ordinal) ||
            toolName.Contains("kcap-flow-result", StringComparison.Ordinal);

        return namesFlowResultServer && toolName.EndsWith("submit_review_result", StringComparison.Ordinal);
    }

    /// <summary>
    /// AI-1292: whether a tool call arriving on a reviewer token is within the reviewer's bound
    /// (read-only) kcap allowlist. A BARE tool name (Codex passes the raw MCP tool name, no server
    /// qualifier) is allowed: the reviewer's Codex MCP config already confines callable tools to the
    /// bound servers, so the token — not the name — is the authorization. A SERVER-QUALIFIED name
    /// (Claude's <c>mcp__&lt;server&gt;__&lt;tool&gt;</c>, hyphens sanitised to underscores) is
    /// allowed only when <c>&lt;server&gt;</c> is in the bound allowlist; otherwise it's an
    /// out-of-allowlist call the caller DENIES (never defers). Matching is hyphen/underscore- and
    /// case-insensitive so <c>kcap-review</c> and <c>kcap_review</c> compare equal.
    /// </summary>
    static bool IsReviewerToolAllowed(string toolName, string[] boundAllowlist) {
        const string prefix = "mcp__";

        if (!toolName.StartsWith(prefix, StringComparison.Ordinal)) return true;   // bare name → config-lock bounded

        var afterPrefix = toolName[prefix.Length..];
        var sep         = afterPrefix.IndexOf("__", StringComparison.Ordinal);

        if (sep <= 0) return false;   // malformed qualified name → deny (fail-safe)

        var server = afterPrefix[..sep].Replace('-', '_');

        foreach (var allowed in boundAllowlist)
            if (string.Equals(server, allowed.Replace('-', '_'), StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }

    static string BuildHookResponseJson(PermissionDecision decision, string vendor) =>
        vendor switch {
            "claude" => BuildClaudeResponse(decision),
            "codex"  => BuildCodexResponse(decision),
            _        => throw new InvalidOperationException($"Unsupported vendor: {vendor}")
        };

    static string BuildClaudeResponse(PermissionDecision decision) {
        // Mirrors the server-side BuildHookResponse. Claude expects camelCase keys here
        // (hookSpecificOutput, hookEventName, applyPermissions, updatedInput) — these are
        // outside the server's snake_case JSON convention because Claude defines them.
        var decisionNode = new JsonObject { ["behavior"] = decision.Behavior };

        if (decision.ApplyPermissions is { } ap) decisionNode["applyPermissions"] = JsonNode.Parse(ap.GetRawText());
        if (decision.UpdatedInput is { } ui) decisionNode["updatedInput"]         = JsonNode.Parse(ui.GetRawText());

        var payload = new JsonObject {
            ["hookSpecificOutput"] = new JsonObject {
                ["hookEventName"] = "PermissionRequest",
                ["decision"]      = decisionNode
            }
        };

        return payload.ToJsonString();
    }

    static string BuildCodexResponse(PermissionDecision decision) {
        // Codex only consumes behavior — strip applyPermissions and updatedInput so the
        // response stays valid for Codex's stricter hook schema.
        var payload = new JsonObject {
            ["hookSpecificOutput"] = new JsonObject {
                ["hookEventName"] = "PermissionRequest",
                ["decision"]      = new JsonObject { ["behavior"] = decision.Behavior }
            }
        };

        return payload.ToJsonString();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Local permission bridge listening on {BaseUrl}")]
    static partial void LogBridgeStarted(ILogger logger, string baseUrl);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Local permission bridge bind attempt {Attempt} on port {Port} failed: {Reason} — retrying")]
    static partial void LogBindRetry(ILogger logger, int attempt, int port, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "RequestPermission via SignalR failed for session {SessionId}; falling back to deny")]
    static partial void LogRequestPermissionFailed(ILogger logger, Exception exception, string sessionId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Auto-approved review-flow result submission for session {SessionId} (vendor={Vendor}) without surfacing a prompt")]
    static partial void LogFlowResultAutoApproved(ILogger logger, string sessionId, string vendor);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Denied out-of-allowlist tool {ToolName} for unattended reviewer session {SessionId}")]
    static partial void LogReviewerToolDenied(ILogger logger, string sessionId, string toolName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Permission bridge handler error")]
    static partial void LogBridgeHandlerError(ILogger logger, Exception exception);
}
