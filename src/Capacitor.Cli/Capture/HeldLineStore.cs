using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Skills;

namespace Capacitor.Cli.Capture;

/// <summary>
/// One source's held line, so a restarted watcher resumes its retry ladder instead of starting it
/// again. Holds only the line's coordinate and hash: the transcript keeps the line itself.
/// </summary>
internal sealed class HeldLineStore(string path, string sessionId, string? agentId) {
    const string DirName = "held-lines";

    public HeldLineStore(ConfigRoot config, string sessionId, string? agentId)
        : this(config.Path(DirName, FileName(sessionId, agentId)), sessionId, agentId) { }

    public string SessionId => sessionId;
    public string? AgentId => agentId;
    internal string FilePath => path;

    public HeldLine? Load() {
        try {
            if (!File.Exists(path)) return null;
            var held = JsonSerializer.Deserialize(File.ReadAllText(path), CaptureJsonContext.Default.HeldLine);
            return held is not null && held.SessionId == sessionId && held.AgentId == agentId ? held : null;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
            return null;
        }
    }

    // A failed write only costs the ladder: the line stays held in memory and in the transcript.
    public void Save(HeldLine held) {
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            AtomicFile.Replace(path, JsonSerializer.Serialize(held, CaptureJsonContext.Default.HeldLine));
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public void Delete() {
        try {
            File.Delete(path);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    static string FileName(string sessionId, string? agentId) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{sessionId}\n{agentId}")))[..32] + ".json";
}
