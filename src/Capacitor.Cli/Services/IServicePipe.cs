namespace Capacitor.Cli.Services;

interface IServicePipe {
    /// <summary>Null when the service did not answer within <paramref name="timeout"/></summary>
    ServicePipeResponse? Send(string verb, TimeSpan timeout);
}
