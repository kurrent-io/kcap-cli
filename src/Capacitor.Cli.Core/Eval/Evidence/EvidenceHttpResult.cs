namespace Capacitor.Cli.Core.Eval.Evidence;

/// <summary>An evidence route's answer; status 0 means the request never reached the server, with the reason as body.</summary>
public sealed record EvidenceHttpResult(int Status, string Body) {
    public bool IsSuccess => Status is >= 200 and < 300;
}
