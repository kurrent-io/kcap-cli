namespace Capacitor.Cli.Core.LocalEdition;

/// <summary>What answered a request for <c>/healthz/ready</c> on the installation's port. Only
/// <see cref="Foreign"/> is evidence of another program; the rest say nothing about who holds the port.</summary>
public abstract record LocalPortAnswer {
    LocalPortAnswer() { }

    /// <summary>The connection was refused: nothing listens there.</summary>
    public sealed record Refused : LocalPortAnswer {
        public static Refused Instance { get; } = new();
    }

    /// <summary>A connection, but no answer in time or one that ended early. A server busy starting does this too.</summary>
    public sealed record Silent(string Reason) : LocalPortAnswer;

    /// <summary>An answer the kcap server can give: 200, 503 or another server error.</summary>
    public sealed record Answered : LocalPortAnswer {
        public static Answered Instance { get; } = new();
    }

    /// <summary>An answer the kcap server never gives: not HTTP, or a status its health route never returns.</summary>
    /// <param name="What">The program as the answer names it, ready to follow "port N answers as".</param>
    public sealed record Foreign(string What) : LocalPortAnswer;
}
