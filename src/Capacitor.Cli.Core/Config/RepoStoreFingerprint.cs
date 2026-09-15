namespace Capacitor.Cli.Core.Config;

/// <summary>The identity of <c>repos.json</c>'s content at one instant. Two equal fingerprints mean
/// the same bytes. A reference type so a field holding the last one advertised can be swapped
/// atomically from any thread.</summary>
public sealed record RepoStoreFingerprint(string ContentHash);
