using System.Diagnostics;
using System.Text.Json.Nodes;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>
/// End-to-end stdio JSON-RPC tests for <c>kcap mcp flows</c>.
/// Spawns the freshly-built CLI binary, points it at a WireMock-stubbed
/// Capacitor server (via <c>KCAP_URL</c>), seeds an isolated config
/// directory (via <c>KCAP_CONFIG_DIR</c>) so token/profile state never
/// leaks between tests, and asserts on the wire-level JSON-RPC envelopes
/// the server emits plus the HTTP calls WireMock observed.
/// </summary>
public class McpFlowsServerTests : IDisposable {
    readonly WireMockServer _server           = WireMockServer.Start();
    readonly string         _cfgDir           = Path.Combine(Path.GetTempPath(), $"kcap-flows-cfg-{Guid.NewGuid():N}");
    readonly string         _cwdDir           = Path.Combine(Path.GetTempPath(), $"kcap-flows-cwd-{Guid.NewGuid():N}");
    readonly List<Process>  _spawnedProcesses = [];

    public McpFlowsServerTests() {
        Directory.CreateDirectory(_cfgDir);
        Directory.CreateDirectory(_cwdDir);

        // Initialize a git repo in _cwdDir so GitRepository.FindRoot returns a path,
        // and create a subdirectory to verify requesting_cwd vs requesting_repo_root.
        InitGitRepo(_cwdDir);
    }

    public void Dispose() {
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
    /// Initialises a minimal git repo (git init + git remote add origin) so
    /// <c>GitRepository.FindRoot</c> finds a root and <c>RepositoryDetection</c>
    /// can parse an owner/name from the remote URL.
    /// </summary>
    static void InitGitRepo(string dir) {
        RunGit(dir, "init");
        RunGit(dir, "remote add origin https://github.com/test-owner/test-repo.git");
    }

    static void RunGit(string cwd, string args) {
        using var p = Process.Start(new ProcessStartInfo("git", args) {
            WorkingDirectory      = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false
        })!;
        p.WaitForExit(5000);
    }

    /// <summary>
    /// Resolves the path of the built `kcap` CLI binary in the source tree.
    /// Mirrors the approach in <see cref="McpSessionsServerTests"/>.
    /// </summary>
    static string GetCliBinaryPath() {
        var asmDir      = Path.GetDirectoryName(typeof(McpFlowsServerTests).Assembly.Location)!;
        var binDir      = Path.GetDirectoryName(asmDir)!;
        var config      = Path.GetFileName(binDir);
        var testBin     = Path.GetDirectoryName(binDir)!;
        var testProjDir = Path.GetDirectoryName(testBin)!;
        var testRoot    = Path.GetDirectoryName(testProjDir)!;
        var repoRoot    = Path.GetDirectoryName(testRoot)!;
        var binaryName  = OperatingSystem.IsWindows() ? "kcap.exe" : "kcap";
        return Path.Combine(repoRoot, "src", "Capacitor.Cli", "bin", config, "net10.0", binaryName);
    }

    /// <summary>
    /// Spawns <c>kcap mcp flows</c> as a child process with WireMock as the backend.
    /// </summary>
    Process SpawnMcpServer(string provider = "None", string? workingDirectory = null) {
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

        var psi = new ProcessStartInfo(binary, "mcp flows") {
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            WorkingDirectory       = workingDirectory ?? _cwdDir,
            Environment = {
                ["KCAP_URL"]        = _server.Url!,
                ["KCAP_CONFIG_DIR"] = _cfgDir
            }
        };

        var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start kcap process");
        _spawnedProcesses.Add(process);
        return process;
    }

    /// <summary>
    /// Spawns the server with KCAP_SESSION_ID set so requester context includes a session ID.
    /// </summary>
    Process SpawnMcpServerWithSession(string sessionId, string provider = "None", string? workingDirectory = null) {
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

        var psi = new ProcessStartInfo(binary, "mcp flows") {
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            WorkingDirectory       = workingDirectory ?? _cwdDir,
            Environment = {
                ["KCAP_URL"]          = _server.Url!,
                ["KCAP_CONFIG_DIR"]   = _cfgDir,
                ["KCAP_SESSION_ID"]   = sessionId
            }
        };

        var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start kcap process");
        _spawnedProcesses.Add(process);
        return process;
    }

    static async Task<JsonObject> SendRequest(Process proc, JsonObject request, TimeSpan? timeout = null) {
        await proc.StandardInput.WriteLineAsync(request.ToJsonString());
        await proc.StandardInput.FlushAsync();

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
    public async Task Initialize_returns_kcap_flows_server_info() {
        using var proc = SpawnMcpServer();
        try {
            var response = await SendRequest(proc, InitializeRequest(1));

            await Assert.That(response["id"]?.GetValue<int>()).IsEqualTo(1);
            var result = response["result"]?.AsObject();
            await Assert.That(result).IsNotNull();
            await Assert.That(result!["serverInfo"]?["name"]?.GetValue<string>()).IsEqualTo("kcap-flows");
            await Assert.That(result["protocolVersion"]?.GetValue<string>()).IsEqualTo("2024-11-05");
        } finally {
            await ShutdownAsync(proc);
        }
    }

    [Test]
    public async Task Tools_list_returns_four_flow_tools() {
        using var proc = SpawnMcpServer();
        try {
            var response = await SendRequest(proc, ToolsListRequest(2));

            var tools = response["result"]?["tools"]?.AsArray();
            await Assert.That(tools).IsNotNull();
            await Assert.That(tools!.Count).IsEqualTo(4);

            var names = tools.Select(t => t?["name"]?.GetValue<string>()).ToHashSet();
            await Assert.That(names.Contains("start_review_flow")).IsTrue();
            await Assert.That(names.Contains("submit_review_round")).IsTrue();
            await Assert.That(names.Contains("get_review_flow_status")).IsTrue();
            await Assert.That(names.Contains("close_review_flow")).IsTrue();
        } finally {
            await ShutdownAsync(proc);
        }
    }

    /// <summary>
    /// Core scenario: verifies that start_review_flow posts to /api/flows/review/start,
    /// that the POST body includes the resolved requester context (requesting_repo_root = git root,
    /// requesting_session_id from KCAP_SESSION_ID), and that the MCP tool response surfaces
    /// both the flow_run_id/status envelope and the FINDINGS result text from the server.
    /// </summary>
    [Test]
    public async Task Start_review_flow_posts_requester_context_and_returns_plain_text_result() {
        const string flowRunId  = "flow-abc-123";
        const string roundId    = "round-001";
        const string sessionId  = "claude-session-aabbccdd";

        var stubbedResponse = $$"""
            {
              "flow_run_id": "{{flowRunId}}",
              "round_id": "{{roundId}}",
              "round_number": 1,
              "status": "completed",
              "result_kind": "FINDINGS",
              "result_text": "## Review findings\n\nThe spec looks good.",
              "reviewer_agent_id": null,
              "reviewer_session_id": null
            }
            """;

        _server.Given(
            Request.Create()
                .WithPath("/api/flows/review/start")
                .UsingPost()
        ).RespondWith(
            Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(stubbedResponse)
        );

        // Create a subdirectory inside _cwdDir so the server starts there — we can then
        // verify requesting_repo_root points to _cwdDir (the git root) while requesting_cwd
        // points to the subdirectory.
        var subdir = Path.Combine(_cwdDir, "src", "feature");
        Directory.CreateDirectory(subdir);

        using var proc = SpawnMcpServerWithSession(sessionId, workingDirectory: subdir);
        try {
            var args = new JsonObject {
                ["kind"]         = "spec-review",
                ["target_kind"]  = "spec",
                ["target_ref"]   = "docs/feature.md",
                ["target_title"] = "Feature spec",
                ["context"]      = "Please review this spec for completeness."
            };

            var response = await SendRequest(proc, ToolsCallRequest(3, "start_review_flow", args));

            // Assert the MCP response is a success
            var result = response["result"]?.AsObject();
            await Assert.That(result).IsNotNull();
            await Assert.That(result!["isError"]?.GetValue<bool>()).IsNotEqualTo(true);

            // Assert the response text contains the envelope fields
            var text = result["content"]?[0]?["text"]?.GetValue<string>();
            await Assert.That(text).IsNotNull();
            await Assert.That(text!.Contains($"flow_run_id: {flowRunId}")).IsTrue();
            await Assert.That(text.Contains("status: completed")).IsTrue();
            await Assert.That(text.Contains("result_kind: FINDINGS")).IsTrue();

            // Assert the response text contains the result text from the server
            await Assert.That(text.Contains("## Review findings")).IsTrue();
            await Assert.That(text.Contains("The spec looks good.")).IsTrue();

            // Assert the POST was made to the correct endpoint
            var hits = _server.FindLogEntries(
                Request.Create().WithPath("/api/flows/review/start").UsingPost()
            );
            await Assert.That(hits.Count).IsEqualTo(1);

            // Verify the POST body includes the requester context fields
            var body = hits[0].RequestMessage.Body ?? "";
            var bodyNode = JsonNode.Parse(body)?.AsObject();
            await Assert.That(bodyNode).IsNotNull();

            // requesting_session_id: from KCAP_SESSION_ID (stripped of dashes)
            var reqSessionId = bodyNode!["requesting_session_id"]?.GetValue<string>();
            await Assert.That(reqSessionId).IsNotNull();
            await Assert.That(reqSessionId!.Contains("claudesessionaabbccdd") || reqSessionId.Contains("claude-session-aabbccdd")).IsTrue();

            // requesting_cwd: the subdirectory the server was started in
            var reqCwd = bodyNode["requesting_cwd"]?.GetValue<string>();
            await Assert.That(reqCwd).IsNotNull();
            // cwd should be either subdir or contain "src/feature"
            await Assert.That(
                reqCwd!.Contains(Path.Combine("src", "feature")) ||
                reqCwd.Equals(subdir, StringComparison.OrdinalIgnoreCase)
            ).IsTrue();

            // requesting_repo_root: the git root (= _cwdDir, where git init was run).
            // On macOS directory paths can differ between what the test creates
            // (Path.GetTempPath() / Guid) and what the spawned process sees via
            // Directory.GetCurrentDirectory() (which resolves symlinks on some platforms).
            // We verify the invariant: the repo root is a parent of the subdir, and it
            // contains the unique test-directory name so it can't be an arbitrary system dir.
            var reqRepoRoot = bodyNode["requesting_repo_root"]?.GetValue<string>();
            await Assert.That(reqRepoRoot).IsNotNull();
            // The unique GUID portion of _cwdDir must appear somewhere in the repo root path
            // (both paths point to the same directory, just possibly via different symlink chains).
            var cwdDirName = Path.GetFileName(_cwdDir.TrimEnd(Path.DirectorySeparatorChar));
            await Assert.That(reqRepoRoot!.Contains(cwdDirName, StringComparison.OrdinalIgnoreCase)).IsTrue();

            // kind: matches what we passed
            await Assert.That(bodyNode["kind"]?.GetValue<string>()).IsEqualTo("spec-review");
            await Assert.That(bodyNode["target_kind"]?.GetValue<string>()).IsEqualTo("spec");
            await Assert.That(bodyNode["context"]?.GetValue<string>()).IsEqualTo("Please review this spec for completeness.");
        } finally {
            await ShutdownAsync(proc);
        }
    }

    [Test]
    public async Task Get_review_flow_status_calls_correct_endpoint_and_surfaces_envelope() {
        const string flowRunId = "flow-status-xyz";

        var stubbedStatus = $$"""
            {
              "flow_run_id": "{{flowRunId}}",
              "definition_id": "spec-review",
              "status": "completed",
              "target_title": "My Spec",
              "round_count": 2,
              "last_result_kind": "APPROVED",
              "last_result_text": "Approved with minor comments."
            }
            """;

        _server.Given(
            Request.Create()
                .WithPath($"/api/flows/{flowRunId}")
                .UsingGet()
        ).RespondWith(
            Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(stubbedStatus)
        );

        using var proc = SpawnMcpServer();
        try {
            var args     = new JsonObject { ["flow_run_id"] = flowRunId };
            var response = await SendRequest(proc, ToolsCallRequest(4, "get_review_flow_status", args));

            var result = response["result"]?.AsObject();
            await Assert.That(result).IsNotNull();
            await Assert.That(result!["isError"]?.GetValue<bool>()).IsNotEqualTo(true);

            var text = result["content"]?[0]?["text"]?.GetValue<string>();
            await Assert.That(text).IsNotNull();
            await Assert.That(text!.Contains($"flow_run_id: {flowRunId}")).IsTrue();
            await Assert.That(text.Contains("status: completed")).IsTrue();
            await Assert.That(text.Contains("Approved with minor comments.")).IsTrue();

            var hits = _server.FindLogEntries(
                Request.Create().WithPath($"/api/flows/{flowRunId}").UsingGet()
            );
            await Assert.That(hits.Count).IsEqualTo(1);
        } finally {
            await ShutdownAsync(proc);
        }
    }

    [Test]
    public async Task Submit_review_round_posts_to_rounds_endpoint() {
        const string flowRunId = "flow-round-abc";
        const string roundId   = "round-002";

        var stubbedResponse = $$"""
            {
              "flow_run_id": "{{flowRunId}}",
              "round_id": "{{roundId}}",
              "round_number": 2,
              "status": "completed",
              "result_kind": "APPROVED",
              "result_text": "Changes look good now.",
              "reviewer_agent_id": null,
              "reviewer_session_id": null
            }
            """;

        _server.Given(
            Request.Create()
                .WithPath($"/api/flows/{flowRunId}/rounds")
                .UsingPost()
        ).RespondWith(
            Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(stubbedResponse)
        );

        using var proc = SpawnMcpServer();
        try {
            var args = new JsonObject {
                ["flow_run_id"] = flowRunId,
                ["context"]     = "I have addressed all feedback."
            };
            var response = await SendRequest(proc, ToolsCallRequest(5, "submit_review_round", args));

            var result = response["result"]?.AsObject();
            await Assert.That(result).IsNotNull();
            await Assert.That(result!["isError"]?.GetValue<bool>()).IsNotEqualTo(true);

            var text = result["content"]?[0]?["text"]?.GetValue<string>();
            await Assert.That(text).IsNotNull();
            await Assert.That(text!.Contains($"flow_run_id: {flowRunId}")).IsTrue();
            await Assert.That(text.Contains("round_id:")).IsTrue();
            await Assert.That(text.Contains("Changes look good now.")).IsTrue();

            var hits = _server.FindLogEntries(
                Request.Create().WithPath($"/api/flows/{flowRunId}/rounds").UsingPost()
            );
            await Assert.That(hits.Count).IsEqualTo(1);

            // Verify the POST body has context
            var body     = hits[0].RequestMessage.Body ?? "";
            var bodyNode = JsonNode.Parse(body)?.AsObject();
            await Assert.That(bodyNode).IsNotNull();
            await Assert.That(bodyNode!["context"]?.GetValue<string>()).IsEqualTo("I have addressed all feedback.");
        } finally {
            await ShutdownAsync(proc);
        }
    }

    [Test]
    public async Task Close_review_flow_posts_to_close_endpoint() {
        const string flowRunId = "flow-close-abc";

        _server.Given(
            Request.Create()
                .WithPath($"/api/flows/{flowRunId}/close")
                .UsingPost()
        ).RespondWith(
            Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody($$"""{"flow_run_id":"{{flowRunId}}","status":"closed"}""")
        );

        using var proc = SpawnMcpServer();
        try {
            var args     = new JsonObject { ["flow_run_id"] = flowRunId };
            var response = await SendRequest(proc, ToolsCallRequest(6, "close_review_flow", args));

            var result = response["result"]?.AsObject();
            await Assert.That(result).IsNotNull();
            await Assert.That(result!["isError"]?.GetValue<bool>()).IsNotEqualTo(true);

            var text = result["content"]?[0]?["text"]?.GetValue<string>();
            await Assert.That(text).IsNotNull();
            await Assert.That(text!.Contains($"flow_run_id: {flowRunId}")).IsTrue();
            await Assert.That(text.Contains("status: closed")).IsTrue();
            // FormatCloseResponse must NOT emit round_id or result_kind lines
            await Assert.That(text.Contains("round_id:")).IsFalse();
            await Assert.That(text.Contains("result_kind:")).IsFalse();

            var hits = _server.FindLogEntries(
                Request.Create().WithPath($"/api/flows/{flowRunId}/close").UsingPost()
            );
            await Assert.That(hits.Count).IsEqualTo(1);
        } finally {
            await ShutdownAsync(proc);
        }
    }

    [Test]
    public async Task Submit_review_round_without_flow_run_id_returns_error() {
        using var proc = SpawnMcpServer();
        try {
            var args = new JsonObject {
                ["context"] = "Some context but no flow ID"
            };
            var response = await SendRequest(proc, ToolsCallRequest(7, "submit_review_round", args));

            var result = response["result"]?.AsObject();
            await Assert.That(result).IsNotNull();
            await Assert.That(result!["isError"]?.GetValue<bool>()).IsTrue();

            var text = result["content"]?[0]?["text"]?.GetValue<string>();
            await Assert.That(text).IsNotNull();
            await Assert.That(text!.Contains("flow_run_id")).IsTrue();
        } finally {
            await ShutdownAsync(proc);
        }
    }
}
