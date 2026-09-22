using System.Text.Json;
using Capacitor.Cli.Daemon.Acp;
using Capacitor.Cli.Daemon.Harness.Pi;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Pi;

/// <summary>
/// Drives the real <c>PiRpcHostedAgentRuntimeFactory.BuildPsi</c> reviewer builder against a real
/// <c>pi --mode rpc</c> child and a scripted local provider — the C# bench for the containment cert.
/// One scratch root holds an isolated <c>HOME</c> and <c>PI_CODING_AGENT_DIR</c> (the one deliberate
/// difference from a production launch, applied in <see cref="RunAsync"/>) plus a worktree carrying a
/// canary in every place Pi discovers context (operator and repository extensions, skills, prompt
/// templates, system-prompt and AGENTS files), so a canary that never surfaces proves the reviewer
/// suppressed that source. Alongside them sit a sibling directory and symlinks a contained file-tool
/// implementation must refuse, and a stub MCP result server standing in for <c>kcap mcp flow-result</c>.
/// </summary>
internal sealed class PiContainmentBench : IAsyncDisposable {
    readonly TempDir _root;

    string _resultServerWrapper = "";
    string _npmInvokedLog       = "";

    public string Home              { get; private set; } = "";
    public string AgentDir          { get; private set; } = "";
    public string Worktree          { get; private set; } = "";
    public string StateDir          { get; private set; } = "";
    public string Outside           { get; private set; } = "";
    public string HostileSessionDir { get; private set; } = "";
    public string ResultLog         { get; private set; } = "";

    public PiScriptedProvider Provider { get; } = new();

    /// <summary>Argv lines the recording <c>npmCommand</c> stand-in saw, read fresh each time: the
    /// real child writes this file during its own startup, after <c>Create</c> already returned.</summary>
    public IReadOnlyList<string> NpmInvocations =>
        File.Exists(_npmInvokedLog) ? File.ReadAllLines(_npmInvokedLog) : [];

    PiContainmentBench(TempDir root) => _root = root;

    public static PiContainmentBench Create(bool plantCanaries, bool resultServerServesSubmit = true) {
        var bench = new PiContainmentBench(new TempDir("pi-cert"));
        bench.Setup(plantCanaries, resultServerServesSubmit);
        return bench;
    }

    void Setup(bool plantCanaries, bool resultServerServesSubmit) {
        Home              = _root.CreateDir("home");
        AgentDir          = _root.CreateDir("agent");
        Worktree          = _root.CreateDir("work");
        StateDir          = _root.PathTo("state");
        Outside           = _root.CreateDir("outside");
        HostileSessionDir = _root.PathTo("evil-sessions");

        File.WriteAllText(Path.Combine(Outside, "secret.txt"), "OUTSIDE-SECRET\n");
        File.WriteAllText(Path.Combine(Worktree, "inside.txt"), "INSIDE-FILE\n");

        // A sibling whose name merely starts with the worktree's — a string-prefix check would admit
        // it, which is exactly the mistake the extension's realpath+relative comparison must not make.
        var evilSibling = Worktree + "-evil";
        Directory.CreateDirectory(evilSibling);
        File.WriteAllText(Path.Combine(evilSibling, "secret.txt"), "OUTSIDE-SECRET\n");

        File.CreateSymbolicLink(Path.Combine(Worktree, "leak"), Path.Combine(Outside, "secret.txt"));
        Directory.CreateDirectory(Path.Combine(Worktree, "sub"));
        Directory.CreateSymbolicLink(Path.Combine(Worktree, "sub", "linkdir"), Outside);

        WriteProviderConfig();

        var settings = new Dictionary<string, object?> {
            ["defaultProvider"]      = "probe",
            ["defaultModel"]         = "m1",
            ["defaultThinkingLevel"] = "off",
        };

        if (plantCanaries) PlantInventory(settings);

        File.WriteAllText(Path.Combine(AgentDir, "settings.json"), JsonSerializer.Serialize(settings));

        WriteResultServer(resultServerServesSubmit);
    }

    void WriteProviderConfig() {
        var models = new object[] {
            new { id = "m1", name = "m1", reasoning = false, input = new[] { "text" }, contextWindow = 32000, maxTokens = 4096 },
            new { id = "m2", name = "m2", reasoning = false, input = new[] { "text" }, contextWindow = 32000, maxTokens = 4096 },
        };

        var modelsJson = JsonSerializer.Serialize(new {
            providers = new Dictionary<string, object> {
                ["probe"] = new {
                    baseUrl    = Provider.BaseUrl,
                    api        = "openai-completions",
                    apiKey     = "probe-key",
                    authHeader = true,
                    models,
                    compat = new { supportsDeveloperRole = false },
                },
            },
        });

        File.WriteAllText(Path.Combine(AgentDir, "models.json"), modelsJson);
    }

    // One canary per discovery source the probe measured, so a plant that never surfaces means
    // suppression and not a dead canary planted wrong.
    void PlantInventory(Dictionary<string, object?> settings) {
        // operator (agent-dir) scope
        WriteCanary(Path.Combine(AgentDir, "extensions", "opext.ts"), "opext");
        WriteCanary(_root.PathTo("declared", "opdeclared.ts"), "opdeclared");
        WriteCanary(_root.PathTo("pkg", "oppkg.ts"), "oppkg");
        settings["extensions"] = new[] { "../declared/opdeclared.ts" };

        _npmInvokedLog = _root.PathTo("npm-invoked.log");
        var fakeNpm = _root.PathTo("fake-npm.sh");
        File.WriteAllText(fakeNpm, $"#!/bin/sh\necho \"$@\" >> \"{_npmInvokedLog}\"\nexit 1\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(fakeNpm, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        // A local-path package (loads directly) alongside an npm-managed one (needs an install this
        // launch's --offline must refuse) — the two ways `packages` can name something.
        settings["packages"]   = new[] { "../pkg/oppkg.ts", "npm:kcap-probe-nonexistent-package" };
        settings["npmCommand"] = new[] { fakeNpm };

        PlantSkill(_root.PathTo("declared-skills"), "INV-OP-SKILL-DECLARED");
        settings["skills"] = new[] { "../declared-skills" };
        PlantSkill(Path.Combine(AgentDir, "skills"), "INV-OP-SKILL-AGENTDIR");
        PlantSkill(Path.Combine(Home, ".agents", "skills"), "INV-OP-SKILL-HOME-AGENTS");

        WriteText(Path.Combine(AgentDir, "prompts", "inv-op-template.md"), "INV-OP-TEMPLATE");
        WriteText(Path.Combine(AgentDir, "AGENTS.md"), "INV-OP-AGENTS");
        WriteText(Path.Combine(AgentDir, "APPEND_SYSTEM.md"), "INV-OP-APPEND-SYSTEM");

        // repository scope
        WriteCanary(Path.Combine(Worktree, ".pi", "extensions", "repoext.ts"), "repoext");
        PlantSkill(Path.Combine(Worktree, ".pi", "skills"), "INV-REPO-SKILL-PI");
        PlantSkill(Path.Combine(Worktree, ".agents", "skills"), "INV-REPO-SKILL-AGENTS");
        PlantSkill(_root.PathTo(".agents", "skills"), "INV-REPO-SKILL-ANCESTOR");
        WriteText(Path.Combine(Worktree, ".pi", "prompts", "inv-repo-template.md"), "INV-REPO-TEMPLATE");
        WriteText(Path.Combine(Worktree, "AGENTS.md"), "INV-REPO-AGENTS");
        WriteText(_root.PathTo("CLAUDE.md"), "INV-REPO-ANCESTOR-CLAUDE");
        WriteText(Path.Combine(Worktree, ".pi", "SYSTEM.md"), "INV-REPO-SYSTEM");
        WriteText(Path.Combine(Worktree, ".pi", "APPEND_SYSTEM.md"), "INV-REPO-APPEND-SYSTEM");

        // What a reviewed repository could commit to redirect the session store; the reviewer's own
        // --session-dir must outrank this rather than merely coexist with it.
        Directory.CreateDirectory(Path.Combine(Worktree, ".pi"));
        File.WriteAllText(Path.Combine(Worktree, ".pi", "settings.json"),
            JsonSerializer.Serialize(new { sessionDir = HostileSessionDir }));
    }

    const string CanarySource =
        """
        import { appendFileSync, mkdirSync } from "node:fs";
        import { join } from "node:path";

        const NAME = "__CANARY_NAME__";

        export default function (pi: any) {
          const dir = process.env.PROBE_MARK_DIR;
          if (dir) {
            mkdirSync(dir, { recursive: true });
            appendFileSync(join(dir, NAME + "-loaded"), JSON.stringify({ pid: process.pid }) + "\n");
          }
          pi.registerTool({
            name: NAME + "_tool",
            label: NAME,
            description: "Canary tool. Its presence means the extension loaded.",
            parameters: { type: "object", properties: {} },
            async execute() {
              return { content: [{ type: "text", text: NAME }] };
            },
          });
        }
        """;

    static void WriteCanary(string path, string name) {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, CanarySource.Replace("__CANARY_NAME__", name));
    }

    static void PlantSkill(string skillsRoot, string token) {
        var name = token.ToLowerInvariant();
        var dir  = Path.Combine(skillsRoot, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), $"---\nname: {name}\ndescription: {token}\n---\n{token}\n");
    }

    static void WriteText(string path, string token) {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, token + "\n");
    }

    // A minimal stdio MCP server standing in for `kcap mcp flow-result`: initialize, tools/list,
    // tools/call, recording every call's params to ResultLog. `resultServerServesSubmit: false`
    // drops submit_review_result from tools/list, for the manifest/allowlist-mismatch case.
    void WriteResultServer(bool servesSubmit) {
        ResultLog = _root.PathTo("result-calls.log");
        var scriptPath  = _root.PathTo("stub-result-server.js");
        var wrapperPath = _root.PathTo("stub-result-server.sh");

        var served = servesSubmit
            ? """["submit_review_result","send_flow_message"]"""
            : """["send_flow_message"]""";

        File.WriteAllText(scriptPath, $$"""
            const fs = require("node:fs");
            const RESULT_LOG = {{JsonSerializer.Serialize(ResultLog)}};
            const TOOLS = {{served}};

            function send(obj) { process.stdout.write(JSON.stringify(obj) + "\n"); }

            let buffer = "";
            process.stdin.setEncoding("utf8");
            process.stdin.on("data", (chunk) => {
              buffer += chunk;
              let nl;
              while ((nl = buffer.indexOf("\n")) >= 0) {
                const line = buffer.slice(0, nl).trim();
                buffer = buffer.slice(nl + 1);
                if (!line) continue;
                let msg;
                try { msg = JSON.parse(line); } catch { continue; }
                const { id, method, params } = msg;
                if (method === "initialize") {
                  send({ jsonrpc: "2.0", id, result: {
                    protocolVersion: "2024-11-05", capabilities: { tools: {} },
                    serverInfo: { name: "stub", version: "0" } } });
                } else if (method === "tools/list") {
                  send({ jsonrpc: "2.0", id, result: { tools: TOOLS.map((name) => ({
                    name, description: name, inputSchema: { type: "object", properties: {} } })) } });
                } else if (method === "tools/call") {
                  fs.appendFileSync(RESULT_LOG, JSON.stringify(params) + "\n");
                  send({ jsonrpc: "2.0", id, result: { content: [{ type: "text", text: "recorded" }] } });
                } else if (id !== undefined) {
                  send({ jsonrpc: "2.0", id, result: {} });
                }
              }
            });
            """);

        File.WriteAllText(wrapperPath, $"#!/bin/sh\nexec node \"{scriptPath}\" \"$@\"\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(wrapperPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        _resultServerWrapper = wrapperPath;
    }

    /// <summary>
    /// Builds one launch exactly the way the real factory would, spawns it against real <c>pi</c>,
    /// sends the scripted turn, and reports what the provider and <c>get_state</c> observed.
    /// <paramref name="argvOverride"/>, when given, replaces the argument list <c>BuildPsi</c>
    /// produced — used only by the positive controls that need Pi's OWN default tool set rather than
    /// the reviewer's allowlisted one.
    /// </summary>
    public async Task<PiContainmentRun> RunAsync(IReadOnlyList<string>? argvOverride, params PiScriptedStep[] script) {
        Provider.Script(script);

        var ctx = new RuntimeStartContext(
            AgentId: "cert-agent", Vendor: "pi", SourceRepoPath: Worktree,
            Worktree: new WorktreeInfo(Path: Worktree, Branch: "cert", SourceRepo: Worktree),
            Prompt: null, Model: null, Effort: null, Tools: null,
            IsReview: false, IsReviewFlow: true, Review: null,
            Cols: 80, Rows: 24,
            ServerUrl: "http://kcap.test", DaemonBridgeUrl: null,
            CapacitorPath: _resultServerWrapper) with {
            LaunchIdentity = LaunchIdentity.ForLaunch(aliasResultChannel: false),
        };

        var servers      = AcpReviewFlowMcp.Build(ctx, []);
        var tools        = PiReviewerToolSurface.For(servers);
        var manifestJson = PiReviewerManifest.Build(Worktree, servers, tools);
        var paths        = PiReviewerLaunchDir.Create(StateDir, "cert", Guid.NewGuid().ToString("N"), manifestJson);

        try {
            var psi = PiRpcHostedAgentRuntimeFactory.BuildPsi(new DaemonConfig(), ctx, paths, tools);

            // The one deliberate difference from production: an isolated HOME/agent dir so the cert
            // never touches the operator's own ~/.pi state or reads a real credential.
            psi.Environment["HOME"]                 = Home;
            psi.Environment["PI_CODING_AGENT_DIR"]   = AgentDir;

            if (argvOverride is not null) {
                psi.ArgumentList.Clear();
                foreach (var arg in argvOverride) psi.ArgumentList.Add(arg);
            }

            await using var process = new PiRpcProcess(psi, NullLogger<PiRpcProcess>.Instance, TimeProvider.System);

            await process.WriteLineAsync(PiRpc.GetStateCommand("s0"), CancellationToken.None);

            var lines = process.ReadLinesAsync(CancellationToken.None).GetAsyncEnumerator();

            var stateFrame = await WaitForAsync(lines, f => f.Kind == PiRpcFrameKind.Response && f.Id == "s0",
                TimeSpan.FromSeconds(20));

            var ready = stateFrame?.Success == true;

            string? sessionFile = null;
            if (stateFrame is { } sf && sf.Root.Obj("data") is { } data) sessionFile = data.Str("sessionFile");

            // Only the unmodified reviewer argv loads the reviewer extension that writes ready.json —
            // an argvOverride (the positive controls, which need Pi's own default tool set instead of
            // the reviewer's) never does, so there is nothing to verify agreement against.
            string? mismatch = null;
            if (ready && argvOverride is null) {
                mismatch = PiReviewerReadiness.Verify(paths.Ready, tools);
                if (mismatch is not null) ready = false;
            }

            if (ready) {
                await process.WriteLineAsync(PiRpc.PromptCommand("p1", "Begin the review."), CancellationToken.None);
                await WaitForAsync(lines, f => f.Type == "agent_settled", TimeSpan.FromSeconds(60));
            }

            var requests         = Provider.Requests;
            var toolsOffered     = requests.Count > 0 ? ToolNames(requests[0]) : [];
            var firstRequestText = requests.Count > 0 ? requests[0].GetProperty("messages").GetRawText() : "";
            var toolResults      = requests.Count > 0 ? ToolResultTexts(requests[^1]) : [];

            var stderr = process.Diagnostics ?? "";
            if (mismatch is not null) stderr = stderr.Length > 0 ? $"{stderr}\n{mismatch}" : mismatch;

            await process.TerminateAsync(TimeSpan.FromSeconds(5));

            return new PiContainmentRun(ready, toolsOffered, firstRequestText, toolResults, sessionFile, stderr);
        } finally {
            PiReviewerLaunchDir.Delete(paths.Dir, StateDir);
        }
    }

    static async Task<PiRpcFrame?> WaitForAsync(IAsyncEnumerator<string> lines, Func<PiRpcFrame, bool> predicate, TimeSpan timeout) {
        using var cts = new CancellationTokenSource(timeout);

        try {
            while (await lines.MoveNextAsync().AsTask().WaitAsync(cts.Token)) {
                if (PiRpc.TryParseLine(lines.Current) is { } frame && predicate(frame)) return frame;
            }
        } catch (OperationCanceledException) {
            // Timed out waiting for the predicate to match — the caller reads that as "not ready" /
            // "never settled" rather than as a harness fault.
        }

        return null;
    }

    static IReadOnlyList<string> ToolNames(JsonElement request) =>
        request.TryGetProperty("tools", out var tools) && tools.IsArray
            ? [.. tools.EnumerateArray().Select(t => t.GetProperty("function").GetProperty("name").GetString() ?? "")]
            : [];

    static IReadOnlyList<string> ToolResultTexts(JsonElement request) =>
        request.TryGetProperty("messages", out var messages) && messages.IsArray
            ? [.. messages.EnumerateArray()
                  .Where(m => m.TryGetProperty("role", out var role) && role.GetString() == "tool")
                  .Select(m => m.GetProperty("content").ToString())]
            : [];

    public async ValueTask DisposeAsync() {
        Provider.Dispose();
        _root.Dispose();
        await ValueTask.CompletedTask;
    }
}
