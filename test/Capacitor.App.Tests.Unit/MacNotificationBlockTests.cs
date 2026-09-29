using System.Runtime.InteropServices;
using Capacitor.App.Services.Notifications;

namespace Capacitor.App.Tests.Unit;

public class MacNotificationBlockTests {
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate nint Copy(nint block);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void Release(nint block);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void Authorization(nint block, byte granted, nint error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void Completion(nint block, nint error);

    [Test]
    public async Task Native_copy_keeps_authorization_callback_alive_after_managed_owner_disposes() {
        if (!OperatingSystem.IsMacOS()) return;
        var library = NativeLibrary.Load("/usr/lib/libSystem.B.dylib");
        var copy = Marshal.GetDelegateForFunctionPointer<Copy>(NativeLibrary.GetExport(library, "_Block_copy"));
        var release = Marshal.GetDelegateForFunctionPointer<Release>(NativeLibrary.GetExport(library, "_Block_release"));
        var received = new List<(bool, nint)>();
        var owner = MacNotificationBlock.Authorization((granted, error) => received.Add((granted, error)));
        var retained = copy(owner.Handle);
        owner.Dispose();
        try {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            var invoke = Marshal.GetDelegateForFunctionPointer<Authorization>(Marshal.ReadIntPtr(retained, 16));
            invoke(retained, 1, 123);
            await Assert.That(received.Count).IsEqualTo(1);
            await Assert.That(received[0]).IsEqualTo((true, (nint)123));
        } finally { release(retained); NativeLibrary.Free(library); }
    }

    [Test]
    public async Task Completion_preserves_native_error_and_contains_managed_callback_exceptions() {
        if (!OperatingSystem.IsMacOS()) return;
        nint received = 0;
        using var owner = MacNotificationBlock.Completion(error => {
            received = error;
            throw new InvalidOperationException("simulated native completion failure");
        });
        var invoke = Marshal.GetDelegateForFunctionPointer<Completion>(Marshal.ReadIntPtr(owner.Handle, 16));
        invoke(owner.Handle, 456);
        await Assert.That(received).IsEqualTo((nint)456);
    }
}
