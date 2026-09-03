namespace Capacitor.Cli.Core.Http;

/// <summary>
/// Tags every request with the headers the server's update-notification pipeline reads. Both values
/// are fixed for the process, so they are computed once rather than per request.
/// </summary>
internal sealed class ObservationHeaderHandler : DelegatingHandler {
    readonly string? _version;
    readonly bool    _updateCheckOff;

    public ObservationHeaderHandler(CapacitorServer server) {
        var version = CapacitorVersion.CurrentDisplay();

        _version = !string.IsNullOrWhiteSpace(version) && !version.Equals("unknown", StringComparison.OrdinalIgnoreCase)
            ? version
            : null;

        _updateCheckOff = server.Profiles.Effective?.UpdateCheck == false;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
        if (_version is not null) request.Headers.TryAddWithoutValidation(HttpClientExtensions.CliVersionHeader, _version);

        // Sent ONLY to declare the preference is off; its absence means on.
        if (_updateCheckOff)
            request.Headers.TryAddWithoutValidation(
                HttpClientExtensions.UpdateCheckHeader, HttpClientExtensions.UpdateCheckOffValue);

        return base.SendAsync(request, ct);
    }
}
