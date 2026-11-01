namespace Capacitor.Cli.Services;

/// <summary>The readiness leg a polling verify stopped at, printed as <c>readiness_leg=</c> when it gives
/// up so a timeout says which proof was missing.</summary>
static class ReadinessLeg {
    public const string NoAnswer     = "no_answer";
    public const string WrongDaemon  = "wrong_daemon";
    public const string WrongVersion = "wrong_version";
    public const string NotTaskOwned = "not_task_owned";

    public static string Describe(string leg) => leg switch {
        NoAnswer     => "it never answered on its control socket, so it was still starting or had already exited",
        WrongDaemon  => "what answered under its name was another daemon or protocol",
        WrongVersion => "it answered with a version other than this kcap's",
        NotTaskOwned => "the daemon that answered was not the one the service started",
        _            => leg,
    };
}
