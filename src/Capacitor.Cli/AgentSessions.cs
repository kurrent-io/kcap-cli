using System.Globalization;
using Capacitor.Cli.Core;

namespace Capacitor.Cli;

/// <summary>
/// Which session each coding-agent process is running, so a process below it can name the session.
/// The session whose hook ran last owns the process.
/// </summary>
sealed class AgentSessions(ConfigRoot config, Func<int, int?> parentOf, TimeProvider? time = null) {
    const int MaxHops = 32;

    static readonly TimeSpan ExitRetention = TimeSpan.FromDays(30);

    readonly TimeProvider _time = time ?? TimeProvider.System;

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
        } catch { }
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
    /// Drops every note no live process holds, keeping an exit record for its session: the only local
    /// proof a session's agent is gone when nothing told the server, as for a private daemon agent.
    /// </summary>
    public void Reap() {
        foreach (var pid in Claimants()) {
            if (Of(pid) is not null) continue;

            try {
                if (NotedSession(pid) is { } session) RecordExit(session);
                File.Delete(Note(pid));
            } catch { }
        }

        PruneExitRecords();
    }

    /// <summary>
    /// A live claim wins over an exit record: a session resumed in a new process is running again.
    /// </summary>
    public SessionLiveness Liveness(SessionId session) =>
        IsClaimed(session)              ? SessionLiveness.Running
      : File.Exists(ExitRecord(session)) ? SessionLiveness.Exited
      : SessionLiveness.Unknown;

    /// <summary>When this machine saw <paramref name="session"/>'s agent exit; null without a readable record.</summary>
    public DateTimeOffset? ExitedAt(SessionId session) {
        try {
            var record = ExitRecord(session);

            return File.Exists(record) && TryParseTime(File.ReadAllText(record), out var at) ? at : null;
        } catch {
            return null;
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

    SessionId? NotedSession(int pid) =>
        File.ReadAllText(Note(pid)).Split('\n') is [var session, _] ? SessionId.Parse(session) : null;

    void RecordExit(SessionId session) {
        var record = ExitRecord(session);
        Directory.CreateDirectory(Path.GetDirectoryName(record)!);

        var temp = $"{record}.{Environment.ProcessId}";
        File.WriteAllText(temp, _time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
        File.Move(temp, record, overwrite: true);
    }

    void PruneExitRecords() {
        var cutoff = _time.GetUtcNow() - ExitRetention;

        try {
            foreach (var record in Directory.EnumerateFiles(config.Path("agent-sessions", "exited"))) {
                try {
                    if (!TryParseTime(File.ReadAllText(record), out var at) || at < cutoff)
                        File.Delete(record);
                } catch { }
            }
        } catch { }
    }

    static bool TryParseTime(string text, out DateTimeOffset at) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out at);

    string ExitRecord(SessionId session) => config.Path("agent-sessions", "exited", session.Value);

    string Note(int pid) => config.Path("agent-sessions", pid.ToString(CultureInfo.InvariantCulture));
}
