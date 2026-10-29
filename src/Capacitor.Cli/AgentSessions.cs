using System.Globalization;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Continuation;

namespace Capacitor.Cli;

/// <summary>
/// Which session each coding-agent process is running, so a process below it can name the session.
/// The session whose hook ran last owns the process.
/// </summary>
sealed class AgentSessions(ConfigRoot config, Func<int, int?> parentOf, TimeProvider? time = null) : ISessionLivenessSource {
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

    /// <summary>
    /// A hook firing for <paramref name="session"/> proves it is running, so any exit record for it goes.
    /// <paramref name="origin"/> says where its end belongs once the process is gone. What an earlier claim
    /// recorded stands, the server above all since the session lives where it started; a later claim only
    /// fills what is still missing.
    /// </summary>
    public void Claim(int agentPid, SessionId session, ClaimOrigin? origin = null) {
        try { File.Delete(ExitRecord(session)); } catch { }

        try {
            if (ProcessStartToken.ForPid(agentPid) is not { } token) return;

            var note = Note(agentPid);
            var text = $"{session}\n{token}";
            if (File.Exists(note) && File.ReadAllText(note) == text) {
                if (origin is not null) {
                    var known  = ReadOrigin(agentPid, token);
                    var merged = known is null ? origin : new ClaimOrigin(
                        known.Vendor ?? origin.Vendor, known.Server ?? origin.Server,
                        known.TranscriptPath ?? origin.TranscriptPath, known.Cwd ?? origin.Cwd,
                        known.AgentId ?? origin.AgentId, known.DaemonEpoch ?? origin.DaemonEpoch);
                    if (merged != known) WriteOrigin(agentPid, token, merged);
                }
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(note)!);

            // Replaced whole, so a git hook reading it mid-write never sees half a note.
            var temp = $"{note}.{Environment.ProcessId}";
            File.WriteAllText(temp, text);
            File.Move(temp, note, overwrite: true);
            WriteOrigin(agentPid, token, origin);
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
    /// Drops every note whose process is provably gone, keeping an exit record for its session: the
    /// only local proof a session's agent is gone when nothing told the server, as for a private
    /// daemon agent. A note whose holder cannot be compared stays, since an exit record lets another
    /// session take this one over.
    /// </summary>
    public void Reap() => ReapExited(currentServer: null);

    /// <summary><see cref="Reap"/>, returning the sessions it found gone. An end for a session recorded
    /// against a server other than <paramref name="currentServer"/> is set aside per session, away from
    /// the pid a later process can reuse, and returned by a later pass on that server within the exit
    /// retention.</summary>
    public IReadOnlyList<ExitedSession> ReapExited(string? currentServer) {
        var gone = new List<ExitedSession>();
        foreach (var pid in Claimants()) {
            if (Of(pid) is not null) continue;

            try {
                if (File.ReadAllText(Note(pid)).Split('\n') is [var session, var token]) {
                    if (!HolderIsGone(pid, token)) continue;

                    var origin = ReadOrigin(pid, token);
                    if (SessionId.Parse(session) is { } exited) {
                        RecordExit(exited);
                        if (origin is { Server: { } server } && !SameServer(server, currentServer)) DeferEnd(exited, origin);
                        // A session resumed under another process is running again: no end for it.
                        else if (LiveClaimant(exited) is null) gone.Add(new ExitedSession(exited, origin));
                    }

                    DeleteOrigin(pid, token);
                }

                File.Delete(Note(pid));
            } catch { }
        }

        // A session resumed and gone again since its end was deferred is already here: one end is enough.
        foreach (var deferred in TakeDeferredEnds(currentServer))
            if (!gone.Any(g => string.Equals(g.Session.Value, deferred.Session.Value, StringComparison.OrdinalIgnoreCase)))
                gone.Add(deferred);
        PruneExitRecords();
        return gone;
    }

    void DeferEnd(SessionId session, ClaimOrigin origin) {
        var path = DeferredEnd(session);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Staged outside the folder TakeDeferredEnds lists, since a session id may itself hold a dot.
        var staging = config.Path("agent-sessions", "deferred-ends-staging");
        Directory.CreateDirectory(staging);
        var temp = Path.Combine(staging, $"{Path.GetFileName(path)}.{Environment.ProcessId}");
        File.WriteAllText(temp, Fields(session.Value, origin.Vendor, origin.Server, origin.TranscriptPath, origin.Cwd, origin.AgentId, origin.DaemonEpoch));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>The ends set aside for <paramref name="currentServer"/>, removed as they are returned; one
    /// older than the exit retention is dropped, and one whose session is running again is dropped too.</summary>
    IEnumerable<ExitedSession> TakeDeferredEnds(string? currentServer) {
        var taken = new List<ExitedSession>();
        try {
            foreach (var path in Directory.EnumerateFiles(config.Path("agent-sessions", "deferred-ends"))) {
                try {
                    var stale = _time.GetUtcNow() - File.GetLastWriteTimeUtc(path) >= ExitRetention;
                    // Named case-folded so one session is set aside once; the id as claimed is inside.
                    if (ReadFields(path) is not [var id, var vendor, var server, var transcript, var cwd, .. var hosted]
                     || SessionId.Parse(id) is not { } session) {
                        File.Delete(path);
                        continue;
                    }
                    if (!stale && !SameServer(server, currentServer)) continue;

                    File.Delete(path);
                    if (!stale && LiveClaimant(session) is null)
                        taken.Add(new ExitedSession(session, OriginOf(vendor, server, transcript, cwd, hosted) with { Server = server }));
                } catch { }
            }
        } catch { }
        return taken;
    }

    static string? NullIfEmpty(string value) => value.Length > 0 ? value : null;

    /// <summary>A file written before the hosted fields existed has none of them.</summary>
    static ClaimOrigin OriginOf(string vendor, string server, string transcript, string cwd, string[] hosted) =>
        new(NullIfEmpty(vendor), NullIfEmpty(server), NullIfEmpty(transcript), NullIfEmpty(cwd),
            hosted is [var agentId, ..] ? NullIfEmpty(agentId) : null,
            hosted is [_, var epoch, ..] ? NullIfEmpty(epoch) : null);

    // One field per line, each escaped: a path may hold a newline, and a missing field is empty.
    static string Fields(params string?[] fields) => string.Join('\n', fields.Select(f => Uri.EscapeDataString(f ?? "")));

    static string[] ReadFields(string path) => [.. File.ReadAllText(path).Split('\n').Select(Uri.UnescapeDataString)];

    internal static bool SameServer(string a, string? b) => ServerIdentity.SameServer(a, b);

    /// <summary>
    /// Gone when no process has the pid, or one does under a different start token (the pid was
    /// reused). A live process whose token cannot be read is not proof of anything.
    /// </summary>
    internal static bool HolderIsGone(bool processExists, bool? tokenMatches) => !processExists || tokenMatches == false;

    static bool HolderIsGone(int pid, string token) {
        var alive = ProcessHelpers.IsProcessAlive(pid);
        return HolderIsGone(alive, alive ? ProcessStartToken.Matches(pid, token) : null);
    }

    /// <summary>
    /// Running while any note names the session and its holder is not provably gone, whatever exit
    /// record exists: a resumed session is running again, and a takeover must not rest on a guess.
    /// </summary>
    public SessionLiveness Liveness(SessionId session) =>
        LiveClaimant(session) is not null ? SessionLiveness.Running
      : File.Exists(ExitRecord(session))  ? SessionLiveness.Exited
      :                                     SessionLiveness.Unknown;

    /// <summary>
    /// A process running <paramref name="session"/> that is not provably gone, or null. Hooks may
    /// write the id in another case than the one asked about, so the match ignores case.
    /// </summary>
    public int? LiveClaimant(SessionId session) {
        foreach (var pid in Claimants()) {
            try {
                if (File.ReadAllText(Note(pid)).Split('\n') is [var named, var token]
                 && string.Equals(SessionId.Parse(named)?.Value, session.Value, StringComparison.OrdinalIgnoreCase)
                 && !HolderIsGone(pid, token))
                    return pid;
            } catch { }
        }

        return null;
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
                    if (!DateTimeOffset.TryParse(File.ReadAllText(record), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at) || at < cutoff)
                        File.Delete(record);
                } catch { }
            }
        } catch { }
    }

    string ExitRecord(SessionId session) => config.Path("agent-sessions", "exited", session.Value.ToLowerInvariant());

    string Note(int pid) => config.Path("agent-sessions", pid.ToString(CultureInfo.InvariantCulture));

    string DeferredEnd(SessionId session) => config.Path("agent-sessions", "deferred-ends", session.Value.ToLowerInvariant());

    // Beside the note rather than in it, so a reader of the two-line note is unchanged. Its name is not
    // a pid, so Claimants passes over it.
    string Origin(int pid) => config.Path("agent-sessions", $"{pid.ToString(CultureInfo.InvariantCulture)}.origin");

    // Carries the claim's start token, so an origin written by a later holder of a reused pid is never
    // read as, or deleted for, the claim that was reaped.
    void WriteOrigin(int pid, string token, ClaimOrigin? origin) {
        var path = Origin(pid);
        if (origin is null) {
            if (File.Exists(path)) File.Delete(path);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = $"{path}.{Environment.ProcessId}";
        File.WriteAllText(temp, Fields(token, origin.Vendor, origin.Server, origin.TranscriptPath, origin.Cwd, origin.AgentId, origin.DaemonEpoch));
        File.Move(temp, path, overwrite: true);
    }

    void DeleteOrigin(int pid, string token) {
        try {
            if (ReadFields(Origin(pid)) is [var owner, ..] && owner == token) File.Delete(Origin(pid));
        } catch { }
    }

    internal ClaimOrigin? ReadOrigin(int pid, string token) {
        try {
            return ReadFields(Origin(pid)) is [var owner, var vendor, var server, var transcript, var cwd, .. var hosted] && owner == token
                ? OriginOf(vendor, server, transcript, cwd, hosted)
                : null;
        } catch {
            return null;
        }
    }
}
