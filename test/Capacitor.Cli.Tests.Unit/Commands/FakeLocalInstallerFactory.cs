using Capacitor.Cli.LocalEdition;

namespace Capacitor.Cli.Tests.Unit.Commands;

sealed class FakeLocalInstallerFactory(Func<LocalInstaller> create) : ILocalInstallerFactory {
    public static readonly FakeLocalInstallerFactory Unused =
        new(() => throw new InvalidOperationException("This test never runs a local setup."));

    public LocalInstaller Create() => create();
}
