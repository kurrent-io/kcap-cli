namespace Capacitor.Cli.Daemon.Tests.Unit.Pty.Unix;

/// <summary>A child that sits reading a cooked-mode PTY until it is killed, so the kernel echoes
/// whatever is written. Not <c>cat</c>: uutils coreutils (Ubuntu 26.04's) names its utility after
/// the exec path, which the spawner's fd-bound exec makes <c>/dev/fd/N</c>, and refuses to run.</summary>
static class IdleReader {
    public const string Command = "/bin/sh";

    public static readonly string[] Args = ["-c", "read line"];
}
