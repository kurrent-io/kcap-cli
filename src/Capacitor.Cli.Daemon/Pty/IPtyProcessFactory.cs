namespace Capacitor.Cli.Daemon.Pty;

/// <summary>
/// Single source of truth for the fixed size hosted-agent PTYs are spawned at.
/// Hosted PTYs are never resized; the daemon reports these dims to the server so
/// read-only viewers lock their xterm to exactly the width Claude drew for.
/// Referenced by both <see cref="IPtyProcessFactory.Spawn"/>'s defaults and the
/// orchestrator's spawn + dimension-report calls so the two can never drift.
/// </summary>
public static class PtyDefaults
{
    public const ushort Cols = 120;
    public const ushort Rows = 40;
}

public interface IPtyProcessFactory
{
    IPtyProcess Spawn(string command, string[] args, string cwd,
        Dictionary<string, string>? extraEnv = null, ushort cols = PtyDefaults.Cols, ushort rows = PtyDefaults.Rows);
}
