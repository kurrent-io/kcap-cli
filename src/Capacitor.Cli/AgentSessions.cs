using System.Globalization;
using Capacitor.Cli.Core;

namespace Capacitor.Cli;

/// <summary>
/// Which session each coding-agent process is running, so a process below it can name the session.
/// The session whose hook ran last owns the process.
/// </summary>
sealed class AgentSessions(ConfigRoot config, Func<int, int?> parentOf) {
    const int MaxHops = 32;

    public static AgentSessions OnThisMachine(ConfigRoot config) => new(config, pid => ProcessHelpers.GetProcessInfo(pid)?.ppid);

    /// <summary>
    /// False for a vendor whose agent process is an IDE or a server: it runs several sessions at once
    /// and a person's own terminals, so a claim on it would file their commits under whichever session
    /// ran a hook last.
    /// </summary>
    public static bool HostsOneSession(string vendor) => vendor is not ("antigravity" or "cursor" or "opencode");

    public void Claim(int agentPid, SessionId session) {
        try {
            if (ProcessStartToken.ForPid(agentPid) is not { } token) return;

            var note = Note(agentPid);
            var text = $"{session}\n{token}";
            if (File.Exists(note) && File.ReadAllText(note) == text) return;

            Directory.CreateDirectory(Path.GetDirectoryName(note)!);

            // Replaced whole, so a git hook reading it mid-write never sees half a note.
            var temp = $"{note}.{Environment.ProcessId}";
            File.WriteAllText(temp, text);
            File.Move(temp, note, overwrite: true);
        } catch {
            // ignored
        }
    }

    /// <summary>
    /// Null for a note an earlier holder of the pid left.
    /// </summary>
    public SessionId? Of(int agentPid) {
        try {
            var note = Note(agentPid);

            return File.Exists(note)
                && File.ReadAllText(note).Split('\n') is [var session, var token]
                && ProcessStartToken.Matches(agentPid, token) == true
                    ? SessionId.Parse(session)
                    : null;
        } catch {
            return null;
        }
    }

    /// <summary>
    /// Whether a live agent process runs <paramref name="session"/>, so the git hook files its commits.
    /// </summary>
    public bool IsClaimed(SessionId session) => Claimants().Any(pid => Of(pid) == session);

    /// <summary>
    /// Drops every note no live process holds.
    /// </summary>
    public void Reap() {
        foreach (var pid in Claimants()) {
            if (Of(pid) is null) {
                try { File.Delete(Note(pid)); } catch { }
            }
        }
    }

    /// <summary>
    /// The session of the nearest agent process at or above <paramref name="pid"/>.
    /// </summary>
    public SessionId? Above(int pid) {
        for (var hop = 0; hop < MaxHops && pid > 1; hop++) {
            if (Of(pid) is { } session) return session;
            if (parentOf(pid) is not { } parent) return null;

            pid = parent;
        }

        return null;
    }

    int[] Claimants() {
        try {
            return [
                ..Directory.EnumerateFiles(config.Path("agent-sessions"))
                    .Select(file => int.TryParse(Path.GetFileName(file), NumberStyles.None, CultureInfo.InvariantCulture, out var pid) ? pid : 0)
                    .Where(pid => pid > 0)
            ];
        } catch {
            return [];
        }
    }

    string Note(int pid) => config.Path("agent-sessions", pid.ToString(CultureInfo.InvariantCulture));
}
