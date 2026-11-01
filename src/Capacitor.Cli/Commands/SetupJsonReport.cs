using Capacitor.Cli.LocalEdition;

namespace Capacitor.Cli.Commands;

/// <summary>
/// The JSON lines of <c>kcap setup --json --no-prompt</c>: a <c>step_started</c> and a <c>step_completed</c> per step, the
/// local install's own events inside the server step, and a last line that is <c>done</c> or <c>failed</c>.
/// </summary>
sealed class SetupJsonReport(TextWriter stdout) {
    public const string Server     = "server";
    public const string SignIn     = "sign_in";
    public const string Visibility = "visibility";
    public const string Harnesses  = "harnesses";
    public const string Daemon     = "daemon";
    public const string Intelligence = "intelligence";
    public const string Import     = "import";

    string? _step;

    /// <summary>Whether a <c>failed</c> line has been written, so the run's end does not write a second one.</summary>
    public bool HasFailed { get; private set; }

    public void Started(string step) {
        _step = step;
        Emit(new() { Event = "step_started", Step = step });
    }

    public void Completed(
            string? serverUrl = null, string? provider = null, string? credential = null, string? visibility = null,
            IReadOnlyList<string>? harnesses = null, string? name = null, string? outcome = null) =>
        Emit(new() {
            Event      = "step_completed",
            Step       = _step,
            ServerUrl  = serverUrl,
            Provider   = provider,
            Credential = credential,
            Visibility = visibility,
            Harnesses  = harnesses,
            Name       = name,
            Outcome    = outcome,
        });

    public void Install(LocalInstallEvent e) => Write(LocalSetupJsonLines.Line(e));

    public void Failed(string code, string message) {
        HasFailed = true;
        Emit(new() { Event = "failed", Step = _step, Code = code, Message = message });
    }

    public void Done(string serverUrl) => Emit(new() { Event = "done", ServerUrl = serverUrl });

    void Emit(LocalSetupLine line) => Write(LocalSetupJsonLines.Render(line));

    void Write(string line) {
        stdout.WriteLine(line);
        stdout.Flush();
    }
}
