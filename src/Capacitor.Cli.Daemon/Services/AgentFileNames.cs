using System.Security.Cryptography;
using System.Text;

namespace Capacitor.Cli.Daemon.Services;

/// The file name every per-agent store uses. The agent id crosses the wire unconstrained, so it is
/// hashed rather than interpolated: no `..` or separator can leave the directory, and the PID record
/// and the transcript journal of one agent always share a name.
internal static class AgentFileNames {
    public static string For(string agentId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(agentId ?? ""))).ToLowerInvariant();
}
