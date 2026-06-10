using System.Diagnostics;

namespace Capacitor.Cli.Services;

/// <summary>
/// Minimal synchronous shell-out for service registration tools
/// (launchctl/systemctl/schtasks). Not used in tests — managers' side-effecting
/// methods are the one part not exercised in CI.
/// </summary>
static class ServiceProcess {
    public static (int ExitCode, string StdOut, string StdErr) Run(string file, params string[] args) {
        var psi = new ProcessStartInfo {
            FileName               = file,
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {file}");
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, stdout, stderr);
    }

    /// <summary>Run and throw with captured stderr on non-zero exit.</summary>
    public static void Check(string file, params string[] args) {
        var (code, _, err) = Run(file, args);
        if (code != 0)
            throw new InvalidOperationException($"{file} {string.Join(' ', args)} failed (exit {code}): {err.Trim()}");
    }
}
