namespace Capacitor.Cli.Service.Supervision;

/// <summary>What the supervisor needs of an enrollment: a name for its log and status, and value equality, so a
/// changed enrollment can be told from the one a running process was started under</summary>
public interface ISupervisedEnrollment {
    string Name { get; }
}
