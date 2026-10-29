namespace Capacitor.Cli.Core.Http;

public static class RetryAfterHeader {
    /// How long the server asked us to wait, in either header form: a proxy may rewrite
    /// delta-seconds as an HTTP date. A date is measured against the response's own Date header, so
    /// server clock skew cannot turn the wait negative.
    public static TimeSpan? Read(HttpResponseMessage response, TimeProvider time) => response.Headers.RetryAfter switch {
        { Delta: { } delta } => delta,
        { Date:  { } date  } => Max(date - (response.Headers.Date ?? time.GetUtcNow()), TimeSpan.Zero),
        _                    => null
    };

    static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}
