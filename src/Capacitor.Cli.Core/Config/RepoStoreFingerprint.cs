namespace Capacitor.Cli.Core.Config;

/// <summary>The on-disk identity of <c>repos.json</c> at one instant. Two equal fingerprints mean
/// no write landed between them. A reference type so a field holding the last one advertised can
/// be swapped atomically from any thread.</summary>
public sealed record RepoStoreFingerprint(long Size, long MtimeTicks);
