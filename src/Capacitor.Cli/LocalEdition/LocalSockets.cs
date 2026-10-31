using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Cli.LocalEdition;

/// <summary>
/// The UNIX sockets the local stack binds under its root. The server derives them from the same root: Postgres
/// listens in <c>run/</c>, and the embedded KurrentDB binds <c>kurrent.sock</c> in its data directory,
/// <c>data/kurrentdb/</c>. A path here that disagrees with the bundle lets preflight pass a root whose socket the
/// server cannot bind.
/// </summary>
static class LocalSockets {
    public const string PostgresName  = ".s.PGSQL.5432";
    public const string KurrentDbName = "kurrent.sock";

    public static string Postgres(LocalRoot root)  => Path.Combine(root.Directory, "run", PostgresName);
    public static string KurrentDb(LocalRoot root) => Path.Combine(root.Directory, "data", "kurrentdb", KurrentDbName);

    public static IReadOnlyList<string> All(LocalRoot root) => [Postgres(root), KurrentDb(root)];
}
