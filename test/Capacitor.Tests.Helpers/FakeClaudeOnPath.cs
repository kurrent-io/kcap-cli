namespace Capacitor.Tests.Helpers;

/// <summary>Puts a <c>claude</c> running the given POSIX shell script first on PATH for the test's lifetime. PATH is
/// process-global: a test using this must be a bare <c>[NotInParallel]</c>.</summary>
public sealed class FakeClaudeOnPath : IDisposable {
    readonly TempDir  _bin;
    readonly EnvScope _path;

    public FakeClaudeOnPath(string script) {
        _bin = new TempDir();
        _bin.CreateExecutable("claude", script);
        _path = EnvScope.Exclusive("PATH", _bin.Path + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"));
    }

    public void Dispose() {
        _path.Dispose();
        _bin.Dispose();
    }
}
