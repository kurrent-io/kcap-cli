using Capacitor.Cli.LocalEdition;

namespace Capacitor.Cli.Tests.Unit.Commands;

sealed class FakeLocalInstallerFactory(Func<IProgress<LocalInstallEvent>, LocalInstaller> create) : ILocalInstallerFactory {
    public static readonly FakeLocalInstallerFactory Unused =
        new(_ => throw new InvalidOperationException("This test never runs a local setup."));

    public LocalInstaller Create(IProgress<LocalInstallEvent> progress) => create(progress);
}
