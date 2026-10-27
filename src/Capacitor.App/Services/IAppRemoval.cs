namespace Capacitor.App.Services;

/// What the app removes of itself once the CLI has removed everything else.
public interface IAppRemoval {
    /// The `kcap` links into this bundle that are still there afterwards.
    Task<IReadOnlyList<string>> RemoveLinksAsync(CancellationToken ct);

    /// Logs what it cannot delete and returns false: nothing left here keeps kcap running.
    bool DeleteSupportFiles();

    /// False when the bundle is still in place.
    Task<bool> TrashBundleAsync(CancellationToken ct);

    void RevealBundle();

    /// What the user does when the app could not remove itself.
    string FinishByHand => "Drag kcap to the Trash";

    /// How a finished uninstall reports the app's own removal.
    string Trashed => "Moved the kcap app to the Trash";
}
