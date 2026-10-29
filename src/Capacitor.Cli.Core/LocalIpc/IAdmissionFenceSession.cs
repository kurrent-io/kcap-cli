namespace Capacitor.Cli.Core.LocalIpc;

/// <summary>A rename fence held on a daemon over one open control connection. Disposing it closes the
/// connection, which releases the fence unless it was committed.</summary>
public interface IAdmissionFenceSession : IAsyncDisposable {
    /// <summary>The pid of the daemon process that granted the fence.</summary>
    int? Pid { get; }

    /// <summary>True only once the daemon answered that the commit is on disk.</summary>
    Task<bool> CommitAsync(TimeSpan timeout, CancellationToken ct);

    /// <summary>Asks the daemon to undo the fence, committed or not; true once it answered.</summary>
    Task<bool> AbortAsync(TimeSpan timeout, CancellationToken ct);
}
