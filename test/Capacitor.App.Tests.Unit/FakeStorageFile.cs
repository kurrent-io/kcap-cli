using Avalonia.Platform.Storage;
using NSubstitute;

namespace Capacitor.App.Tests.Unit;

/// Builds a substitute IStorageFile. Avalonia marks the interface [NotClientImplementable] — a
/// hand-written `: IStorageFile` class fails to compile outside Avalonia's own assemblies — but
/// Avalonia.Base grants InternalsVisibleTo to Castle's dynamic-proxy assembly, which is what
/// NSubstitute generates through, so a substitute is the one way to stand one up here.
static class FakeStorageFile {
    public static IStorageFile Of(string name, byte[] bytes) => Of(name, bytes, (ulong?)bytes.LongLength);

    public static IStorageFile Of(string name, byte[] bytes, ulong? reportedSize) {
        var file = Substitute.For<IStorageFile>();
        file.Name.Returns(name);
        file.GetBasicPropertiesAsync().Returns<StorageItemProperties>(new StorageItemProperties(reportedSize, null, null));
        file.OpenReadAsync().Returns<Stream>(new MemoryStream(bytes));
        return file;
    }

    public static IStorageFile Of(string name, ulong? reportedSize) {
        var file = Substitute.For<IStorageFile>();
        file.Name.Returns(name);
        file.GetBasicPropertiesAsync().Returns<StorageItemProperties>(new StorageItemProperties(reportedSize, null, null));
        file.OpenReadAsync().Returns<Stream>(_ => throw new InvalidOperationException($"{name} should not be opened"));
        return file;
    }

    public static IStorageFile Of(string name, Exception openThrows) {
        var file = Substitute.For<IStorageFile>();
        file.Name.Returns(name);
        file.GetBasicPropertiesAsync().Returns<StorageItemProperties>(new StorageItemProperties(0, null, null));
        file.OpenReadAsync().Returns<Stream>(_ => throw openThrows);
        return file;
    }

    public static IStorageFile Of(string name, byte[] bytes, int throwAfterBytes) {
        var file = Substitute.For<IStorageFile>();
        file.Name.Returns(name);
        file.GetBasicPropertiesAsync().Returns<StorageItemProperties>(new StorageItemProperties((ulong)bytes.LongLength, null, null));
        file.OpenReadAsync().Returns<Stream>(new ThrowAfterStream(bytes, throwAfterBytes));
        return file;
    }

    /// Reads normally up to `throwAfter` bytes, then fails the next read — models a file that
    /// goes unreadable partway through, not just on open.
    sealed class ThrowAfterStream(byte[] bytes, int throwAfter) : MemoryStream(bytes) {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) {
            if (Position >= throwAfter) throw new IOException($"simulated failure after {throwAfter} bytes");
            var capped = (int)Math.Min(buffer.Length, throwAfter - Position);
            return await base.ReadAsync(buffer[..capped], cancellationToken);
        }
    }
}
