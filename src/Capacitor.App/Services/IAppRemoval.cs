namespace Capacitor.App.Services;

/// What the app removes of itself once the CLI has removed everything else.
public interface IAppRemoval {
    /// The `kcap` links into this bundle that are still there afterwards.
    Task<IReadOnlyList<string>> RemoveLinksAsync(CancellationToken ct);

    /// Logs what it cannot delete: nothing left here keeps kcap running.
    void DeleteSupportFiles();

    /// False when the bundle is still in place.
    Task<bool> TrashBundleAsync(CancellationToken ct);

    void RevealBundle();

    /// What the user does when the app could not remove itself.
    string FinishByHand => "Drag kcap to the Trash";
}
