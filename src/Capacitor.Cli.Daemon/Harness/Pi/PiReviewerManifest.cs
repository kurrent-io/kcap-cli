using System.Text;
using System.Text.Json;
using Capacitor.Cli.Core.Acp;

namespace Capacitor.Cli.Daemon.Harness.Pi;

/// <summary>What the reviewer extension is told to bridge and register. The extension does no naming or
/// filtering of its own, so this document is the whole of its tool surface.</summary>
internal static class PiReviewerManifest {
    internal static string Build(
            string root, IReadOnlyList<AcpMcpServerSpec> servers, IReadOnlyList<PiReviewerTool> tools) {
        using var buffer = new MemoryStream();

        using (var json = new Utf8JsonWriter(buffer)) {
            json.WriteStartObject();
            json.WriteString("root", root);

            json.WriteStartArray("fileTools");
            foreach (var tool in tools.Where(t => t.ServerName is null)) json.WriteStringValue(tool.PiName);
            json.WriteEndArray();

            json.WriteStartArray("servers");
            foreach (var server in servers) {
                json.WriteStartObject();
                json.WriteString("id", server.Name);
                json.WriteString("command", server.Command);

                json.WriteStartArray("args");
                foreach (var arg in server.Args) json.WriteStringValue(arg);
                json.WriteEndArray();

                json.WriteStartObject("env");
                foreach (var variable in server.Env) json.WriteString(variable.Name, variable.Value);
                json.WriteEndObject();

                json.WriteStartArray("tools");
                foreach (var tool in tools.Where(t => t.ServerName == server.Name)) {
                    json.WriteStartObject();
                    json.WriteString("mcp", tool.McpName);
                    json.WriteString("pi", tool.PiName);
                    json.WriteEndObject();
                }
                json.WriteEndArray();

                json.WriteEndObject();
            }
            json.WriteEndArray();

            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
