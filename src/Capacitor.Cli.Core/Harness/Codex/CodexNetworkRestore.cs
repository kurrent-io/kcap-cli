namespace Capacitor.Cli.Core.Harness.Codex;

/// <summary>What <see cref="CodexConfigToml.RestoreNetworkAccess"/> did. <see cref="Kept"/> names each
/// security-relevant setting left in place, with the reason it could not be safely undone.</summary>
public sealed record CodexNetworkRestore(CodexConfigToml.Change Change, IReadOnlyList<string> Restored, IReadOnlyList<string> Kept) {
    public static readonly CodexNetworkRestore Nothing = new(CodexConfigToml.Change.Unchanged, [], []);
    public static readonly CodexNetworkRestore Failed  = new(CodexConfigToml.Change.Failed, [], []);
}
