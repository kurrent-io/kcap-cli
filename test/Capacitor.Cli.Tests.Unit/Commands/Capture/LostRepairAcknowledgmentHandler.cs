namespace Capacitor.Cli.Tests.Unit.Commands.Capture;

internal sealed class LostRepairAcknowledgmentHandler : DelegatingHandler {
    bool _lost;
    public LostRepairAcknowledgmentHandler() : base(new HttpClientHandler()) { }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
        var response = await base.SendAsync(request, ct);
        if (!_lost && request.RequestUri!.AbsolutePath.EndsWith("/batches", StringComparison.Ordinal)) {
            _lost = true;
            response.Dispose();
            throw new HttpRequestException("Simulated lost acknowledgment after the server accepted the batch.");
        }
        return response;
    }
}
