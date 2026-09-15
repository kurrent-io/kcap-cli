using Avalonia.Platform.Storage;
using NSubstitute;

namespace Capacitor.App.Tests.Unit;

/// See FakeStorageFile: IStorageFolder is [NotClientImplementable] too, so this is a substitute
/// rather than a hand-written class.
static class FakeStorageFolder {
    public static IStorageFolder Of(string name) {
        var folder = Substitute.For<IStorageFolder>();
        folder.Name.Returns(name);
        return folder;
    }
}
