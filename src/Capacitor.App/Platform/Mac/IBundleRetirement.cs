namespace Capacitor.App.Platform.Mac;

/// <summary>
/// Removal of the bundle the installing process runs from, handed to the installed copy. A running
/// single-file app loads assemblies from its own executable on first use, so it must keep its bundle
/// until it exits; the successor removes it afterwards.
/// </summary>
public interface IBundleRetirement {
    void Schedule(KcapBundle bundle);
    void Cancel(string root);
}
