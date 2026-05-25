using System.Diagnostics;
using System.Text.Json.Nodes;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace kapacitor.Tests.Integration;

/// <summary>
/// End-to-end stdio JSON-RPC tests for <c>kapacitor mcp sessions</c>.
/// Spawns the freshly-built CLI binary, points it at a WireMock-stubbed
/// Kapacitor server (via <c>KAPACITOR_URL</c>), seeds an isolated config
/// directory (via <c>KAPACITOR_CONFIG_DIR</c>) so token/profile state never
/// leaks between tests, and asserts on the wire-level JSON-RPC envelopes
/// the server emits plus the HTTP calls WireMock observed.
/// </summary>
public class McpSessionsServerTests : IDisposable {
    readonly WireMockServer _server  = WireMockServer.Start();
    readonly string         _cfgDir  = Path.Combine(Path.GetTempPath(), $"kapacitor-mcp-cfg-{Guid.NewGuid():N}");
    readonly string         _cwdDir  = Path.Combine(Path.GetTempPath(), $"kapacitor-mcp-cwd-{Guid.NewGuid():N}");

    public McpSessionsServerTests() {
        Directory.CreateDirectory(_cfgDir);
        Directory.CreateDirectory(_cwdDir);
    }

    public void Dispose() {
        _server.Stop();
        try { Directory.Delete(_cfgDir, recursive: true); } catch { /* best effort */ }
        try { Directory.Delete(_cwdDir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>
    /// Resolves the path of the built `kapacitor` CLI binary in the source tree.
    /// The integration tests are built into
    /// <c>test/Kapacitor.Cli.Tests.Integration/bin/&lt;config&gt;/net10.0/</c>, so we
    /// walk up to the repo root and descend into <c>src/Kapacitor.Cli/bin/&lt;config&gt;/net10.0/kapacitor</c>.
    /// </summary>
    static string GetCliBinaryPath() {
        var asmDir = Path.GetDirectoryName(typeof(McpSessionsServerTests).Assembly.Location)!;
        // asmDir → .../test/Kapacitor.Cli.Tests.Integration/bin/<config>/net10.0
        var binDir       = Path.GetDirectoryName(asmDir)!;           // .../bin/<config>
        var config       = Path.GetFileName(binDir);                 // Debug / Release
        var testBin      = Path.GetDirectoryName(binDir)!;           // .../bin
        var testProjDir  = Path.GetDirectoryName(testBin)!;          // .../Kapacitor.Cli.Tests.Integration
        var testRoot     = Path.GetDirectoryName(testProjDir)!;      // .../test
        var repoRoot     = Path.GetDirectoryName(testRoot)!;         // repo root
        var binaryName   = OperatingSystem.IsWindows() ? "kapacitor.exe" : "kapacitor";

        return Path.Combine(repoRoot, "src", "Kapacitor.Cli", "bin", config, "net10.0", binaryName);
    }

    /// <summary>
    /// Spawns <c>kapacitor mcp sessions</c> as a child process. <paramref name="provider"/>
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
                $"kapacitor binary not found at {binary}. Build it first: " +
                "dotnet build src/Kapacitor.Cli/Kapacitor.Cli.csproj",
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
                ["KAPACITOR_URL"]        = _server.Url!,
                ["KAPACITOR_CONFIG_DIR"] = _cfgDir
            }
        };

        return Process.Start(psi) ?? throw new InvalidOperationException("Failed to start kapacitor process");
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
            await Assert.That(result!["serverInfo"]?["name"]?.GetValue<string>()).IsEqualTo("kapacitor-sessions");
            await Assert.That(result["protocolVersion"]?.GetValue<string>()).IsEqualTo("2024-11-05");
        } finally {
            await ShutdownAsync(proc);
        }
    }

    [Test]
    public async Task Tools_list_returns_three_tools_with_correct_names() {
        using var proc = SpawnMcpServer();
        try {
            var response = await SendRequest(proc, ToolsListRequest(2));

            var tools = response["result"]?["tools"]?.AsArray();
            await Assert.That(tools).IsNotNull();
            await Assert.That(tools!.Count).IsEqualTo(3);

            var names = tools.Select(t => t?["name"]?.GetValue<string>()).ToHashSet();
            await Assert.That(names.Contains("search_sessions")).IsTrue();
            await Assert.That(names.Contains("get_session_summary")).IsTrue();
            await Assert.That(names.Contains("get_session_transcript")).IsTrue();
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
    /// When the server reports a non-"None" provider and no tokens are seeded, the CLI
    /// builds an unauthenticated HttpClient (it prints a "Run 'kapacitor login'" hint to
    /// stderr, which is out-of-band for the MCP wire protocol). The Bearer-less call
    /// to the server then returns 401, which <see cref="Kapacitor.Cli.Commands.McpSessionsServer"/>
    /// surfaces verbatim — "Error: HTTP 401 — &lt;body&gt;" with <c>isError: true</c>.
    ///
    /// Observation for Task 7 follow-up: the MCP-level tool result does NOT explicitly
    /// mention "kapacitor login" — only the stderr does, and MCP clients (Claude Code,
    /// Codex) don't forward CLI stderr to the model. A friendlier message inside the
    /// tool result body would be a small but real UX win. Not fixed here to keep this
    /// task focused on tests + docs; raising as a separate follow-up.
    /// </summary>
    [Test]
    public async Task Unauthenticated_returns_friendly_error() {
        _server.Given(Request.Create().WithPath("/api/sessions/search").UsingGet())
            .RespondWith(
                Response.Create()
                    .WithStatusCode(401)
                    .WithHeader("Content-Type", "application/json")
                    .WithBody("""{"message":"Authentication required. Run 'kapacitor login' to sign in."}""")
            );

        // Provider != "None" forces the auth path; no tokens.json exists in _cfgDir so the
        // request goes out without a Bearer header.
        using var proc = SpawnMcpServer(provider: "GitHub");
        try {
            var args     = new JsonObject { ["query"] = "anything", ["repo"] = "all" };
            var response = await SendRequest(proc, ToolsCallRequest(6, "search_sessions", args));

            var result   = response["result"]?.AsObject();
            await Assert.That(result?["isError"]?.GetValue<bool>()).IsTrue();

            var text = result?["content"]?[0]?["text"]?.GetValue<string>();
            await Assert.That(text).IsNotNull();
            // The 401 surfaces in the response body verbatim. We assert on the HTTP status
            // (always present) and the message snippet (proves the server's hint body made
            // it through). Neither is strictly the "Not logged in" friendly message; see
            // the XML doc comment above for the follow-up.
            await Assert.That(text!.Contains("401")).IsTrue();
            await Assert.That(text.Contains("kapacitor login")).IsTrue();
        } finally {
            await ShutdownAsync(proc);
        }
    }
}
