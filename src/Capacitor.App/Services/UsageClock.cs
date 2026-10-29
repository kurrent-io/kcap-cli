using System.Reactive.Linq;

namespace Capacitor.App.Services;

/// The tick that ages usage readings ("updated …", "resets in …"). Every subscriber gets the current
/// time at once; the periodic ticks come from one shared timer, emitted on the timer's thread.
public static class UsageClock {
    public static readonly TimeSpan Period = TimeSpan.FromSeconds(30);

    public static IObservable<DateTimeOffset> Create(TimeProvider time) {
        var ticks = Observable.Create<DateTimeOffset>(observer =>
                time.CreateTimer(_ => observer.OnNext(time.GetUtcNow()), null, Period, Period))
            .Publish()
            .RefCount();
        return Observable.Defer(() => ticks.StartWith(time.GetUtcNow()));
    }
}
