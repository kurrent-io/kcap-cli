using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// Assembly-level isolation for <see cref="AuthProviderCache"/>. Unit tests run against the
/// developer's real <c>~/.config/kcap</c> (only the daemons dir is pinned elsewhere), so without
/// this any test whose SUT calls <c>DiscoverProviderAsync</c> could read a provider cached by a
/// prior run (an OS-reused WireMock port would collide) and skip its own <c>/auth/config</c> stub,
/// or pollute the real cache file. Pinning the store to a throwaway temp file per run keeps the
/// cache a clean no-op for every test that doesn't explicitly exercise it.
/// </summary>
public class AuthProviderCacheGlobalSetup {
    static readonly string StoreFile = Path.Combine(
        Path.GetTempPath(),
        "kcap-authprovider-tests-" + Guid.NewGuid().ToString("N")[..8] + ".json"
    );

    [Before(Assembly)]
    public static void PinStore() => AuthProviderCache.OverridePathForTesting = StoreFile;

    [After(Assembly)]
    public static void CleanupStore() {
        AuthProviderCache.OverridePathForTesting = null;
        try { File.Delete(StoreFile); } catch { /* best effort */ }
    }
}
