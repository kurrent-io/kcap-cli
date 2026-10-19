namespace Capacitor.Cli.LocalEdition;

/// <summary>What the service manager says the server's process is doing.</summary>
abstract record UnitActivity {
    UnitActivity() { }

    /// <summary>How many times the service manager has started the process, when it says; only differences between
    /// two readings mean anything.</summary>
    public int? Starts { get; init; }

    /// <summary>Not loaded, or loaded and inactive: nothing will start it until <c>kcap local start</c>.</summary>
    public sealed record Stopped : UnitActivity {
        public static Stopped Instance { get; } = new();
    }

    public sealed record Running(int Pid) : UnitActivity;

    /// <summary>The process exited and the service manager will start it again.</summary>
    public sealed record Restarting(int? LastExitCode) : UnitActivity;

    /// <summary>The process exited and the service manager will not start it again.</summary>
    public sealed record Exited(int? LastExitCode) : UnitActivity;

    public sealed record Unknown(string Reason) : UnitActivity;
}
