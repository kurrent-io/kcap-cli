namespace Capacitor.App.ViewModels;

/// What the main window holds in its one workspace slot, whichever origin the session has.
public interface ISessionWorkspace {
    string AgentId { get; }
    Task TeardownAsync();
}
