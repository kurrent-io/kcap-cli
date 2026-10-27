using Capacitor.Cli.Service.Sessions;

namespace Capacitor.Cli.Service.Tests.Unit.Control;

sealed class FakeEnrollmentIndex : IEnrollmentIndex {
    public readonly HashSet<string> Sids = new(StringComparer.OrdinalIgnoreCase);
    public readonly HashSet<string> Departed = new(StringComparer.OrdinalIgnoreCase);

    public bool Add(string sid) => Sids.Add(sid);
    public void Remove(string sid) => Sids.Remove(sid);
    public void PruneDeparted() => Sids.ExceptWith(Departed);
    public bool IsEmpty => Sids.Count == 0;
}
