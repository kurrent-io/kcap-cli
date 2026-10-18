using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.LocalEdition.Fakes;

/// <summary>Daemons and watchers as an uninstall sees them; each call appends its name to the shared log.</summary>
sealed class FakeOutgoingRecorders(List<string> log) : IOutgoingRecorders {
    public List<string> Busy { get; } = [];

    public bool StopSucceeds { get; set; } = true;

    public Task<IReadOnlyList<string>> BusyAsync() {
        log.Add("recorders:busy?");
        return Task.FromResult<IReadOnlyList<string>>(Busy);
    }

    public Task<bool> StopAsync() {
        log.Add("recorders:stop");
        return Task.FromResult(StopSucceeds);
    }
}
