namespace Capacitor.Cli.Core.Setup;

/// <summary>
/// The report that this user has a working local install. Until the server has one, the web
/// dashboard keeps showing its "run kcap setup" welcome modal; it records the first report and
/// ignores the rest, so sending it again is harmless.
/// </summary>
public static class CliSetupReport {
    public static string Url(string serverUrl) => $"{serverUrl.TrimEnd('/')}/api/users/me/cli-setup";

    /// <summary>
    /// Hand-built on purpose: a typed DTO would inherit <c>CapacitorJsonContext</c>'s SnakeCaseLower
    /// policy and send <c>cli_version</c>, which the endpoint does not read. <c>joinId</c> is omitted
    /// rather than sent as null. Both inputs are ours — an assembly version and 32 hex chars — so
    /// neither can carry a quote that would break the literal.
    /// </summary>
    public static string Body(string? version, string? joinId) {
        var versionJson = version is null ? "null" : $"\"{version}\"";
        var joinJson    = joinId is null ? "" : $",\"joinId\":\"{joinId}\"";

        return $$"""{"cliVersion":{{versionJson}}{{joinJson}}}""";
    }
}
