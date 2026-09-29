using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Capacitor.App.Services.Notifications;

/// Owns a copied Objective-C block. Each native copy owns its own GCHandle, so delayed completion
/// remains valid after the caller releases its copy. Layout follows Clang's Apple Block ABI.
[SupportedOSPlatform("macos")]
internal sealed unsafe partial class MacNotificationBlock : IDisposable {
    const string LibSystem = "/usr/lib/libSystem.B.dylib";
    static readonly nint StackClass = NativeLibrary.GetExport(NativeLibrary.Load(LibSystem), "_NSConcreteStackBlock");
    static readonly nint AuthDescriptor = Descriptor("v24@?0B8@16");
    static readonly nint CompletionDescriptor = Descriptor("v16@?0@8");
    nint _handle;

    [StructLayout(LayoutKind.Sequential)]
    struct Block {
        public nint Isa;
        public int Flags;
        public int Reserved;
        public nint Invoke;
        public nint Descriptor;
        public nint State;
    }

    public nint Handle => _handle;

    MacNotificationBlock(object callback, nint invoke, nint descriptor) {
        var state = GCHandle.Alloc(callback);
        try {
            var block = new Block {
                Isa = StackClass,
                Flags = (1 << 25) | (1 << 30), // copy/dispose helpers and signature
                Invoke = invoke,
                Descriptor = descriptor,
                State = GCHandle.ToIntPtr(state),
            };
            _handle = CopyBlock(ref block);
        } finally { state.Free(); }
    }

    public static MacNotificationBlock Authorization(Action<bool, nint> callback) =>
        new(callback, (nint)(delegate* unmanaged[Cdecl]<nint, byte, nint, void>)&Authorized, AuthDescriptor);

    public static MacNotificationBlock Completion(Action<nint> callback) =>
        new(callback, (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&Completed, CompletionDescriptor);

    // Descriptors are process-lifetime ABI metadata, just like compiler-emitted block descriptors.
    static nint Descriptor(string signature) {
        var descriptor = (nint*)NativeMemory.AllocZeroed(5, (nuint)sizeof(nint));
        descriptor[1] = sizeof(Block);
        descriptor[2] = (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&Copy;
        descriptor[3] = (nint)(delegate* unmanaged[Cdecl]<nint, void>)&Destroy;
        descriptor[4] = Marshal.StringToCoTaskMemUTF8(signature);
        return (nint)descriptor;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static void Copy(nint destination, nint source) => ((Block*)destination)->State =
        GCHandle.ToIntPtr(GCHandle.Alloc(GCHandle.FromIntPtr(((Block*)source)->State).Target));

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static void Destroy(nint block) => GCHandle.FromIntPtr(((Block*)block)->State).Free();

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static void Authorized(nint block, byte granted, nint error) {
        try { ((Action<bool, nint>)GCHandle.FromIntPtr(((Block*)block)->State).Target!)(granted != 0, error); }
        catch (Exception failure) { NativeDesktopNotificationSink.Report(failure); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static void Completed(nint block, nint error) {
        try { ((Action<nint>)GCHandle.FromIntPtr(((Block*)block)->State).Target!)(error); }
        catch (Exception failure) { NativeDesktopNotificationSink.Report(failure); }
    }

    public static void Finish(nint block) => ((delegate* unmanaged[Cdecl]<nint, void>)((Block*)block)->Invoke)(block);
    public static void Present(nint block, nuint options) => ((delegate* unmanaged[Cdecl]<nint, nuint, void>)((Block*)block)->Invoke)(block, options);

    public void Dispose() {
        var handle = Interlocked.Exchange(ref _handle, 0);
        if (handle != 0) ReleaseBlock(handle);
    }

    [LibraryImport(LibSystem, EntryPoint = "_Block_copy")]
    private static partial nint CopyBlock(ref Block block);
    [LibraryImport(LibSystem, EntryPoint = "_Block_release")]
    private static partial void ReleaseBlock(nint block);
}
