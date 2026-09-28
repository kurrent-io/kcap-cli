using System.Diagnostics;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>The real binary answers <c>kcap mcp knowledge</c>: the subcommand dispatches, the server
/// resolves from DI, its handshake names it, it lists the eight tools, and a read scopes to the
/// working directory's repo resolved on the first call.</summary>
public class McpKnowledgeServerStdioTests : IDisposable {
    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }
    [TempConfigRoot]  public required TempConfigRoot  Config  { get; init; }

    readonly WireMockServer _server           = WireMockServer.Start();
    readonly List<Process>  _spawnedProcesses = [];

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
    }

    Process Spawn(string workingDirectory) {
        _server.Given(Request.Create().WithPath("/auth/config").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"provider":"None"}"""));
        var psi = KcapProcess.StartInfo(Daemons.Store, Config.Root, "mcp", "knowledge");
        psi.WorkingDirectory = workingDirectory;
        psi.Environment["KCAP_URL"] = _server.Url!;
        var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start kcap process");
        _spawnedProcesses.Add(process);
        return process;
    }

    [Test]
    public async Task The_handshake_names_the_server_and_a_read_scopes_to_the_working_directorys_repo() {
        using var repo = GitRepo.Create();
        repo.AddRemote("https://github.com/acme/widget.git");
        _server.Given(Request.Create().WithPath("/api/knowledge/skills").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                .WithBody("""{"skills":[],"next_cursor":null}"""));

        using var proc = Spawn(repo.Path);
        try {
            var init = await SendAsync(proc, RpcRequest(1, "initialize", new JsonObject()));
            await Assert.That(init["result"]?["serverInfo"]?["name"]?.GetValue<string>()).IsEqualTo("kcap-knowledge");

            var list = await SendAsync(proc, RpcRequest(2, "tools/list", new JsonObject()));
            await Assert.That(list["result"]!["tools"]!.AsArray().Count).IsEqualTo(8);

            var call = await SendAsync(proc, RpcRequest(3, "tools/call", new JsonObject { ["name"] = "list_skills", ["arguments"] = new JsonObject() }));
            await Assert.That(call["result"]).IsNotNull();
            await Assert.That(call["result"]!["isError"]).IsNull();

            var hits = _server.FindLogEntries(Request.Create().WithPath("/api/knowledge/skills").UsingGet());
            await Assert.That(hits.Count).IsEqualTo(1);
            await Assert.That(hits[0].RequestMessage.RawQuery ?? "")
                .Contains($"scope_id={RepoHashHelper.ComputeRepoHash("acme", "widget")}");
        } finally {
            await ShutdownAsync(proc);
        }
    }

    static JsonObject RpcRequest(int id, string method, JsonObject parameters) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters };

    static async Task<JsonObject> SendAsync(Process proc, JsonObject request) {
        await proc.StandardInput.WriteLineAsync(request.ToJsonString());
        await proc.StandardInput.FlushAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var line = await proc.StandardOutput.ReadLineAsync(cts.Token)
            ?? throw new InvalidOperationException($"MCP server closed stdout. Stderr: {await proc.StandardError.ReadToEndAsync()}");
        return JsonNode.Parse(line)!.AsObject();
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
}
