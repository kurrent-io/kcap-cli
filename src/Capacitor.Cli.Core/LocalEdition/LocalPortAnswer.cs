using System.Net;

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

    /// <summary>An answer the kcap server can give. Only a 200 is ready.</summary>
    /// <param name="Body">The readiness report, or empty when it is too long to be the server's.</param>
    public sealed record Answered(HttpStatusCode Status, string Body) : LocalPortAnswer;

    /// <summary>An answer the kcap server never gives: not HTTP, or a status it never returns from a host that does
    /// not call itself Kestrel.</summary>
    /// <param name="What">The program as the answer names it, ready to follow "port N answers as".</param>
    public sealed record Foreign(string What) : LocalPortAnswer;
}
