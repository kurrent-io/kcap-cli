using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Services;

sealed class FakeServicePipe : IServicePipe {
    public readonly List<string> Verbs = [];
    public Func<string, ServicePipeResponse?> Answer = _ => new ServicePipeResponse(true, Status: new ServicePipeStatus("running", Pid: 4242));

    public ServicePipeResponse? Send(string verb, TimeSpan timeout) {
        Verbs.Add(verb);
        return Answer(verb);
    }
}
