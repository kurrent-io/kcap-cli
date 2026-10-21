using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using Capacitor.App.Services;
using ReactiveUI.Primitives.Reactive.Concurrency;
using ReactiveUI.Reactive;
using ReactiveUI.Reactive.Builder;
using ReactiveUI.Avalonia.Reactive;
using System.Reactive.Concurrency;

namespace Capacitor.App.Tests.Unit;

/// One process-global headless Avalonia session shared by every UI-touching test. The
/// session AND RxSchedulers.MainThreadScheduler are process-wide, so every test using this
/// class must carry [NotInParallel("AvaloniaSession")].
internal static class AvaloniaSession {
    sealed class TestAppBuilder {
        public static AppBuilder BuildAvaloniaApp() =>
            AppBuilder.Configure<Capacitor.App.App>()
                .UseSkia()
                .WithAppFont()
                .UseReactiveUI(_ => { })
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
    }

    /// One application for the whole assembly, not one per test: per-test isolation releases
    /// Dispatcher.UIThread and reclaims it on EVERY dispatch, so the window ClaimUiThread closes
    /// below would reopen for every UI test after it. One application leaves nothing to reclaim,
    /// which is what makes that single claim hold.
    static readonly Lazy<HeadlessUnitTestSession> Session =
        new(static () => HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder), AvaloniaTestIsolationLevel.PerAssembly),
            LazyThreadSafetyMode.ExecutionAndPublication);

    /// Dispatcher.UIThread binds to whichever thread first touches it and owns it for the process.
    /// Building the Avalonia application inside a dispatch pins that binding to the session's own
    /// worker thread; letting a parallel test on a thread-pool thread touch a Dispatcher-bound type
    /// first instead makes the session's later app build fail VerifyAccess in the compositor ctor.
    /// Forcing one dispatch before any test in this assembly runs claims the thread first.
    [Before(Assembly)]
    public static Task ClaimUiThread() {
        // The image fetcher is process-global and would reach for the network; a test that
        // wants bytes back installs its own for the duration.
        MarkdownImages.Fetch = static (_, _) => Task.FromResult<byte[]?>(null);
        return DispatchAsync(static () => { });
    }

    static AvaloniaScheduler? _uiScheduler;
    static IScheduler? _pinned;

    static void EnsureReactiveUI() {
        if (_uiScheduler is null) {
            ReactiveUIBuilder.ResetBuilderStateForTests();
            RxAppBuilder.CreateReactiveUIBuilder().WithAvalonia().WithCoreServices().BuildApp();
            // AvaloniaScheduler.Instance captures its Dispatcher at type init, which under a
            // headless session is not Dispatcher.UIThread.
            _uiScheduler = new AvaloniaScheduler(Dispatcher.UIThread);
        }
        RxSchedulers.MainThreadScheduler = _pinned ?? _uiScheduler;
    }

    public static Task<T> DispatchAsync<T>(Func<T> body) =>
        Session.Value.Dispatch(() => { EnsureReactiveUI(); return body(); }, CancellationToken.None);

    public static Task DispatchAsync(Action body) =>
        Session.Value.Dispatch(() => { EnsureReactiveUI(); body(); return true; }, CancellationToken.None);

    public static Task<T> DispatchAsync<T>(Func<Task<T>> body) =>
        Session.Value.Dispatch(() => { EnsureReactiveUI(); return body(); }, CancellationToken.None);

    /// Pins RxSchedulers.MainThreadScheduler to an immediate System.Reactive IScheduler for
    /// the body and RESTORES the prior scheduler in finally (it is process-global). This is
    /// also the flavor pin: it only compiles if the scheduler IS a System.Reactive IScheduler
    /// consumed by ObserveOn. (The ambient scheduler lives on `RxSchedulers`; this ReactiveUI line
    /// has no `RxApp` scheduler properties.)
    public static Task WithImmediateRxScheduler(Func<Task> body) => WithRxScheduler(ImmediateScheduler.Instance, body);

    static async Task WithRxScheduler(IScheduler scheduler, Func<Task> body) {
        // Start the process-wide session before snapshotting "prior": outside a dispatch nothing
        // has configured MainThreadScheduler yet, and the finally below would restore that
        // unconfigured default over the real one for every later test.
        _ = Session.Value;

        IScheduler prior = RxSchedulers.MainThreadScheduler;
        _pinned = scheduler;
        RxSchedulers.MainThreadScheduler = scheduler;
        try { await body(); } finally {
            _pinned = null;
            RxSchedulers.MainThreadScheduler = prior;
        }
    }

    /// The standard wrapper for UI tests: the body runs inside DispatchAsync, whose pumped loop a
    /// Dispatcher.UIThread.InvokeAsync hop needs, with ObserveOn(RxSchedulers.MainThreadScheduler)
    /// applying synchronously on the UI thread and marshalled to it from any other thread.
    public static Task RunOnUiAsync(Func<Task> body) =>
        DispatchAsync(async () => {
            await WithRxScheduler(new UiThreadImmediateScheduler(_uiScheduler!), body);
            return true;
        });

    /// What the OS does when another app takes focus; headless Show() leaves a window active. The
    /// platform layer's Deactivated hook is internal to Avalonia, so it is reached through the
    /// interface map by member name.
    public static void LoseKeyboardFocus(Avalonia.Controls.Window window) {
        var impl = window.PlatformImpl!;
        var contract = impl.GetType().GetInterfaces().First(i => i.Name == "IWindowBaseImpl");
        var map = impl.GetType().GetInterfaceMap(contract);
        var getter = Array.FindIndex(map.InterfaceMethods, m => m.Name == "get_Deactivated");
        (map.TargetMethods[getter].Invoke(impl, null) as Action)?.Invoke();
    }
}
