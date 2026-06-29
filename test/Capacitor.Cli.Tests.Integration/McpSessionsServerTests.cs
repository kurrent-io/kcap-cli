using System.Diagnostics;
using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>
/// End-to-end stdio JSON-RPC tests for <c>kcap mcp sessions</c>.
/// Spawns the freshly-built CLI binary, points it at a WireMock-stubbed
/// Capacitor server (via <c>KCAP_URL</c>), seeds an isolated config
/// directory (via <c>KCAP_CONFIG_DIR</c>) so token/profile state never
/// leaks between tests, and asserts on the wire-level JSON-RPC envelopes
/// the server emits plus the HTTP calls WireMock observed.
/// </summary>
public class McpSessionsServerTests : IDisposable {
    readonly WireMockServer _server            = WireMockServer.Start();
    readonly string         _cfgDir            = Path.Combine(Path.GetTempPath(), $"kcap-mcp-cfg-{Guid.NewGuid():N}");
    readonly string         _cwdDir            = Path.Combine(Path.GetTempPath(), $"kcap-mcp-cwd-{Guid.NewGuid():N}");
    readonly List<Process>  _spawnedProcesses  = [];

    public McpSessionsServerTests() {
        Directory.CreateDirectory(_cfgDir);
        Directory.CreateDirectory(_cwdDir);
    }

    public void Dispose() {
        // Safety net: per-test `using`/`finally` blocks should already shut down processes,
        // but track + sweep here so a throw between Process.Start and the using-wrap can't leak.
        foreach (var p in _spawnedProcesses) {
            try {
                if (!p.HasExited) p.Kill(entireProcessTree: true);
                p.Dispose();
            } catch {
                // best-effort cleanup
            }
        }

        _server.Stop();
        try { Directory.Delete(_cfgDir, recursive: true); } catch { /* best effort */ }
        try { Directory.Delete(_cwdDir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>
    /// Resolves the path of the built `kcap` CLI binary in the source tree.
    /// The integration tests are built into
    /// <c>test/Capacitor.Cli.Tests.Integration/bin/&lt;config&gt;/net10.0/</c>, so we
    /// walk up to the repo root and descend into <c>src/Capacitor.Cli/bin/&lt;config&gt;/net10.0/kcap</c>.
    /// </summary>
    static string GetCliBinaryPath() {
        var asmDir = Path.GetDirectoryName(typeof(McpSessionsServerTests).Assembly.Location)!;
        // asmDir → .../test/Capacitor.Cli.Tests.Integration/bin/<config>/net10.0
        var binDir       = Path.GetDirectoryName(asmDir)!;           // .../bin/<config>
        var config       = Path.GetFileName(binDir);                 // Debug / Release
        var testBin      = Path.GetDirectoryName(binDir)!;           // .../bin
        var testProjDir  = Path.GetDirectoryName(testBin)!;          // .../Capacitor.Cli.Tests.Integration
        var testRoot     = Path.GetDirectoryName(testProjDir)!;      // .../test
        var repoRoot     = Path.GetDirectoryName(testRoot)!;         // repo root
        var binaryName   = OperatingSystem.IsWindows() ? "kcap.exe" : "kcap";

        return Path.Combine(repoRoot, "src", "Capacitor.Cli", "bin", config, "net10.0", binaryName);
    }

    /// <summary>
    /// Spawns <c>kcap mcp sessions</c> as a child process. <paramref name="provider"/>
    /// controls the response to <c>/auth/config</c> — "None" lets the server skip token
    /// resolution entirely; "GitHub" forces token-store consultation so the unauthenticated
    /// path can be exercised.
    /// </summary>
    Process SpawnMcpServer(string provider = "None") {
        // Auth discovery stub — primed before spawn so the child sees a response when it asks.
        _server.Given(Request.Create().WithPath("/auth/config").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody($$"""{"provider":"{{provider}}"}"""));

        var binary = GetCliBinaryPath();

        if (!File.Exists(binary)) {
            throw new FileNotFoundException(
                $"kcap binary not found at {binary}. Build it first: " +
                "dotnet build src/Capacitor.Cli/Capacitor.Cli.csproj",
                binary
            );
        }

        var psi = new ProcessStartInfo(binary, "mcp sessions") {
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            WorkingDirectory       = _cwdDir,
            Environment = {
                ["KCAP_URL"]        = _server.Url!,
                ["KCAP_CONFIG_DIR"] = _cfgDir
            }
        };

        var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start kcap process");
        _spawnedProcesses.Add(process);

        return process;
    }

    static async Task<JsonObject> SendRequest(Process proc, JsonObject request, TimeSpan? timeout = null) {
        await proc.StandardInput.WriteLineAsync(request.ToJsonString());
        await proc.StandardInput.FlushAsync();

        // Use a bounded read so a hung child doesn't deadlock the test suite.
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(15));
        var line      = await proc.StandardOutput.ReadLineAsync(cts.Token);

        if (line is null) {
            var stderr = await proc.StandardError.ReadToEndAsync();
            throw new InvalidOperationException($"MCP server closed stdout without responding. Stderr: {stderr}");
        }

        return JsonNode.Parse(line)?.AsObject()
            ?? throw new InvalidOperationException($"Could not parse response as JSON object: {line}");
    }

    static async Task ShutdownAsync(Process proc) {
        try { proc.StandardInput.Close(); } catch { /* already closed */ }
        try {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await proc.WaitForExitAsync(cts.Token);
        } catch {
            try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
        }
    }

    static JsonObject InitializeRequest(int id) => new() {
        ["jsonrpc"] = "2.0",
        ["id"]      = id,
        ["method"]  = "initialize",
        ["params"]  = new JsonObject()
    };

    static JsonObject ToolsListRequest(int id) => new() {
        ["jsonrpc"] = "2.0",
        ["id"]      = id,
        ["method"]  = "tools/list",
        ["params"]  = new JsonObject()
    };

    static JsonObject ToolsCallRequest(int id, string name, JsonObject arguments) => new() {
        ["jsonrpc"] = "2.0",
        ["id"]      = id,
        ["method"]  = "tools/call",
        ["params"]  = new JsonObject {
            ["name"]      = name,
            ["arguments"] = arguments
        }
    };

    [Test]
    public async Task Initialize_returns_server_info_with_correct_name() {
        using var proc = SpawnMcpServer();
        try {
            var response = await SendRequest(proc, InitializeRequest(1));

            await Assert.That(response["id"]?.GetValue<int>()).IsEqualTo(1);
            var result = response["result"]?.AsObject();
            await Assert.That(result).IsNotNull();
            await Assert.That(result!["serverInfo"]?["name"]?.GetValue<string>()).IsEqualTo("kcap-sessions");
            await Assert.That(result["protocolVersion"]?.GetValue<string>()).IsEqualTo("2024-11-05");
        } finally {
            await ShutdownAsync(proc);
        }
    }

    [Test]
    public async Task Tools_list_returns_four_tools_with_correct_names() {
        using var proc = SpawnMcpServer();
        try {
            var response = await SendRequest(proc, ToolsListRequest(2));

            var tools = response["result"]?["tools"]?.AsArray();
            await Assert.That(tools).IsNotNull();
            await Assert.That(tools!.Count).IsEqualTo(4);

            var names = tools.Select(t => t?["name"]?.GetValue<string>()).ToHashSet();
            await Assert.That(names.Contains("search_sessions")).IsTrue();
            await Assert.That(names.Contains("get_session_summary")).IsTrue();
            await Assert.That(names.Contains("get_session_transcript")).IsTrue();
            await Assert.That(names.Contains("get_turn")).IsTrue();
        } finally {
            await ShutdownAsync(proc);
        }
    }

    [Test]
    public async Task Search_sessions_calls_server_and_passes_through_response() {
        const string stubbedBody = """{"hits":[{"session_id":"abc","title":"Batch import","snippet":"batch …"}]}""";

        _server.Given(Request.Create().WithPath("/api/sessions/search").UsingGet())
            .RespondWith(
                Response.Create()
                    .WithStatusCode(200)
                    .WithHeader("Content-Type", "application/json")
                    .WithBody(stubbedBody)
            );

        using var proc = SpawnMcpServer();
        try {
            var args = new JsonObject {
                ["query"] = "batch",
                // explicit "all" to bypass cwd repo-hash detection; keeps the test independent
                // of whatever git state happens to surround the test process.
                ["repo"]  = "all"
            };

            var response = await SendRequest(proc, ToolsCallRequest(3, "search_sessions", args));

            // Returned JSON-RPC envelope wraps a content array; assert on the body it ships back.
            var content = response["result"]?["content"]?[0];
            await Assert.That(content?["type"]?.GetValue<string>()).IsEqualTo("text");
            await Assert.That(content?["text"]?.GetValue<string>()).IsEqualTo(stubbedBody);

            var hits = _server.FindLogEntries(Request.Create().WithPath("/api/sessions/search").UsingGet());
            await Assert.That(hits.Count).IsEqualTo(1);

            var rawUrl   = hits[0].RequestMessage.RawQuery ?? "";
            await Assert.That(rawUrl.Contains("q=batch")).IsTrue();
            // "repo=all" is a sentinel — must NOT be propagated as a real filter.
            await Assert.That(rawUrl.Contains("repo=")).IsFalse();
        } finally {
            await ShutdownAsync(proc);
        }
    }

    [Test]
    public async Task Get_session_summary_projects_recap_to_summary_text_and_plan() {
        const string recap = """[{"type":"whats_done","content":"did X"},{"type":"plan","content":"do Y"}]""";

        _server.Given(
            Request.Create()
                .WithPath("/api/sessions/abc/recap")
                .WithParam("chain", "false")
                .UsingGet()
        ).RespondWith(
            Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(recap)
        );

        using var proc = SpawnMcpServer();
        try {
            var args     = new JsonObject { ["session_id"] = "abc" };
            var response = await SendRequest(proc, ToolsCallRequest(4, "get_session_summary", args));

            var text = response["result"]?["content"]?[0]?["text"]?.GetValue<string>();
            await Assert.That(text).IsNotNull();

            var projected = JsonNode.Parse(text!)?.AsObject();
            await Assert.That(projected).IsNotNull();
            await Assert.That(projected!["summary_text"]?.GetValue<string>()).IsEqualTo("did X");
            await Assert.That(projected["plan"]?.GetValue<string>()).IsEqualTo("do Y");
        } finally {
            await ShutdownAsync(proc);
        }
    }

    [Test]
    public async Task Get_session_transcript_passes_around_event_and_agent_id_through() {
        const string stubbedBody = """{"events":[{"index":42,"speaker":"user","text":"hi"}]}""";

        _server.Given(Request.Create().WithPath("/api/sessions/abc/transcript").UsingGet())
            .RespondWith(
                Response.Create()
                    .WithStatusCode(200)
                    .WithHeader("Content-Type", "application/json")
                    .WithBody(stubbedBody)
            );

        using var proc = SpawnMcpServer();
        try {
            var args = new JsonObject {
                ["session_id"]   = "abc",
                ["around_event"] = 42,
                ["agent_id"]     = "agent-xyz"
            };

            var response = await SendRequest(proc, ToolsCallRequest(5, "get_session_transcript", args));

            var text = response["result"]?["content"]?[0]?["text"]?.GetValue<string>();
            await Assert.That(text).IsEqualTo(stubbedBody);

            var hits = _server.FindLogEntries(Request.Create().WithPath("/api/sessions/abc/transcript").UsingGet());
            await Assert.That(hits.Count).IsEqualTo(1);

            var rawQuery = hits[0].RequestMessage.RawQuery ?? "";
            await Assert.That(rawQuery.Contains("around_event=42")).IsTrue();
            await Assert.That(rawQuery.Contains("agent_id=agent-xyz")).IsTrue();
        } finally {
            await ShutdownAsync(proc);
        }
    }

    /// <summary>
    /// Writes a non-expired token to the per-test config dir's token store so the
    /// CLI's <c>HttpClientExtensions.CreateAuthenticatedClientAsync</c> attaches a
    /// Bearer header. Exercises the long-lived-server path (the MCP server holds a
    /// single HttpClient for the agent's whole session).
    /// </summary>
    void SeedToken(string accessToken = "seed-token") {
        var tokensDir  = Path.Combine(_cfgDir, "tokens");
        Directory.CreateDirectory(tokensDir);
        var tokenJson = $$"""
            {
              "access_token": "{{accessToken}}",
              "expires_at": "{{DateTimeOffset.UtcNow.AddHours(1):O}}",
              "github_username": "seed-user",
              "provider": "GitHubApp"
            }
            """;
        File.WriteAllText(Path.Combine(tokensDir, "default.json"), tokenJson);
    }

    /// <summary>
    /// Regression for the "token refresh is never picked up after startup" bug.
    /// The MCP server caches a single <c>HttpClient</c> for the whole agent session;
    /// if the auth header expires mid-session, every tool call returned the friendly
    /// 401 message until the server was restarted. The fix retries once on 401 after
    /// calling <c>TokenStore.GetValidTokensAsync</c>.
    ///
    /// We seed a non-expired token in the per-test config dir and stub WireMock so
    /// the first call returns 401 and the second returns 200. The retry re-reads
    /// the token (which hasn't actually expired, so it's returned as-is) and resends —
    /// proving the retry path runs at all. (Exercising the real refresh-token flow
    /// would require stubbing the GitHub refresh endpoint as well, which is out of
    /// scope for this regression.)
    /// </summary>
    [Test]
    public async Task Refreshed_token_succeeds_after_401() {
        const string stubbedBody = """{"hits":[{"session_id":"abc","title":"OK"}]}""";
        const string scenario    = "auth-retry";

        _server.Given(Request.Create().WithPath("/api/sessions/search").UsingGet())
            .InScenario(scenario)
            .WillSetStateTo("after-401")
            .RespondWith(Response.Create().WithStatusCode(401).WithBody(""));

        _server.Given(Request.Create().WithPath("/api/sessions/search").UsingGet())
            .InScenario(scenario)
            .WhenStateIs("after-401")
            .RespondWith(
                Response.Create()
                    .WithStatusCode(200)
                    .WithHeader("Content-Type", "application/json")
                    .WithBody(stubbedBody)
            );

        SeedToken();

        using var proc = SpawnMcpServer(provider: "GitHubApp");
        try {
            var args     = new JsonObject { ["query"] = "anything", ["repo"] = "all" };
            var response = await SendRequest(proc, ToolsCallRequest(7, "search_sessions", args));

            var result = response["result"]?.AsObject();
            // Must be a success — the 401 was retried and the second call succeeded.
            await Assert.That(result?["isError"]?.GetValue<bool>()).IsNotEqualTo(true);

            var content = result?["content"]?[0]?["text"]?.GetValue<string>();
            await Assert.That(content).IsEqualTo(stubbedBody);

            var hits = _server.FindLogEntries(Request.Create().WithPath("/api/sessions/search").UsingGet());
            await Assert.That(hits.Count).IsEqualTo(2);
        } finally {
            await ShutdownAsync(proc);
        }
    }

    /// <summary>
    /// Spec compliance: when the server returns 401, <see cref="Capacitor.Cli.Commands.McpSessionsServer"/>
    /// must surface the exact friendly message "Not logged in. Run 'kcap login' on the host shell."
    /// inside the MCP tool result (with <c>isError: true</c>) — not the raw HTTP body.
    ///
    /// MCP clients (Claude Code, Codex) don't forward CLI stderr to the model, so the
    /// stderr hint emitted by <c>HttpClientExtensions.CreateAuthenticatedClientAsync</c>
    /// is invisible to the agent; the friendly message has to live in the tool result.
    ///
    /// The WireMock 401 stub returns an EMPTY body so the assertion proves the message
    /// comes from <c>McpSessionsServer</c>, not from server-body bleed-through.
    /// </summary>
    [Test]
    public async Task Unauthenticated_returns_friendly_error() {
        _server.Given(Request.Create().WithPath("/api/sessions/search").UsingGet())
            .RespondWith(
                Response.Create()
                    .WithStatusCode(401)
                    .WithHeader("Content-Type", "application/json")
                    .WithBody("")
            );

        // Provider != "None" forces the auth path; no tokens.json exists in _cfgDir so the
        // request goes out without a Bearer header.
        using var proc = SpawnMcpServer(provider: "GitHub");
        try {
            var args     = new JsonObject { ["query"] = "anything", ["repo"] = "all" };
            var response = await SendRequest(proc, ToolsCallRequest(6, "search_sessions", args));

            var result   = response["result"]?.AsObject();
            await Assert.That(result?["isError"]?.GetValue<bool>()).IsTrue();

            var content = result?["content"]?[0]?["text"]?.GetValue<string>();
            await Assert.That(content).IsEqualTo(McpSessionsServer.NotLoggedInMessage);
        } finally {
            await ShutdownAsync(proc);
        }
    }
}
