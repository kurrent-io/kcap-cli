namespace Capacitor.Cli.LocalEdition;

/// <summary>The other copies of kcap on this machine that would talk to a local installation.</summary>
interface ILocalClientCopies {
    IReadOnlyList<ClientCopy> Find();
}
