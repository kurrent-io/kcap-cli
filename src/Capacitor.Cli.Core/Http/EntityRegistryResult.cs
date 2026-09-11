namespace Capacitor.Cli.Core.Http;

/// <summary>What reading or writing the registry can mean. <see cref="NotFound"/> covers a repo this
/// profile cannot see and a withdrawal with no declared row to withdraw, indistinguishably — whether
/// a repo has a registry is itself a disclosure. <see cref="Rejected"/> is a name or kind the server
/// will not store; a registry row IS the admission decision, so an unreadable one would admit
/// nothing while looking like an answer.</summary>
public abstract record EntityRegistryResult {
    public sealed record Found(CliEntityRegistry Registry) : EntityRegistryResult;

    public sealed record NotFound : EntityRegistryResult;

    public sealed record Forbidden(string? ErrorCode) : EntityRegistryResult;

    public sealed record Rejected(string Detail) : EntityRegistryResult;
}
