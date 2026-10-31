namespace Capacitor.Cli.Core.LocalEdition;

/// <summary>The other copies of kcap on this machine that would talk to a local installation.</summary>
public interface ILocalClientCopies {
    IReadOnlyList<ClientCopy> Find();
}
