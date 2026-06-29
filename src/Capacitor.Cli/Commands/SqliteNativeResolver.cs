using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Capacitor.Cli.Commands;

/// <summary>
/// Loads the native SQLite engine (<c>e_sqlite3</c>) for <c>kcap import --opencode</c>
/// on demand.
///
/// <para>The AOT-published <c>kcap</c> binary does NOT statically link SQLitePCLRaw's
/// native engine, and — unlike <c>libpty_shim</c> — we deliberately do not ship it inside
/// the npm platform packages: SQLite is only needed to read OpenCode's <c>opencode.db</c>
/// during import, so bundling ~1.6&#160;MB into every install would be wasteful. Instead,
/// the first time SQLite is used we download the correct per-RID native library from the
/// matching GitHub release, verify it against a pinned SHA-256, cache it under
/// <c>~/.cache/kcap</c>, and load it.</para>
///
/// <para>Default OS resolution is attempted first, so dev/standalone builds that already
/// have the library next to the binary load it directly and never touch the network.</para>
///
/// <para>Airgapped/offline installs can point <c>KCAP_SQLITE_NATIVE_BASE_URL</c> at an
/// internal HTTP mirror or a local directory containing the per-RID asset, or simply
/// pre-seed the cache path printed in the error message.</para>
/// </summary>
internal static class SqliteNativeResolver {
    const string LibraryName = "e_sqlite3";

    /// <summary>
    /// Pinned to the <c>SQLitePCLRaw.lib.e_sqlite3</c> version that
    /// <c>SQLitePCLRaw.bundle_e_sqlite3</c> (see <c>Directory.Packages.props</c>) restores.
    /// Doubles as the cache-bucket key so bumping the engine re-fetches instead of loading a
    /// stale lib. If you bump the bundle, regenerate <see cref="Assets"/> hashes — the
    /// <c>SqliteNativeResolverTests</c> pin guard fails until you do.
    /// </summary>
    internal const string EngineVersion = "3.50.3";

    internal sealed record NativeAsset(string FileName, string AssetName, string Sha256);

    /// <summary>RID → the pristine native library shipped by SQLitePCLRaw, by SHA-256.</summary>
    internal static readonly IReadOnlyDictionary<string, NativeAsset> Assets =
        new Dictionary<string, NativeAsset>(StringComparer.Ordinal) {
            ["osx-arm64"]        = new("libe_sqlite3.dylib", "libe_sqlite3-osx-arm64.dylib",      "c2e6eb6f3acdd204502111f158400e65bf7af73c30869a238e7dc9e5c704265c"),
            ["linux-x64"]        = new("libe_sqlite3.so",    "libe_sqlite3-linux-x64.so",         "63ed8433123cf71158ecdb7981abcb54401193d0d1bf80562633888cd21d02c4"),
            ["linux-arm64"]      = new("libe_sqlite3.so",    "libe_sqlite3-linux-arm64.so",       "ac6cbb9e3b7cd33ebfe7fb16da09dc6bfb2258426ef40e42980ed1e744ae517b"),
            ["linux-musl-x64"]   = new("libe_sqlite3.so",    "libe_sqlite3-linux-musl-x64.so",    "11f22cda735dc861348cd070746ae2c55e90369ff80afdfb9e2107d1120cbcb1"),
            ["linux-musl-arm64"] = new("libe_sqlite3.so",    "libe_sqlite3-linux-musl-arm64.so",  "daedee2db762691d6ba3119833698739b1975fd08bd6dc3feb609ff90d1252b1"),
            ["win-x64"]          = new("e_sqlite3.dll",      "e_sqlite3-win-x64.dll",             "3061b1c0e66be7ba1a3223b5b3af90aff353c9bca07bd0a02aefbe2dfbacb81f"),
        };

    static int _registered;

    /// <summary>
    /// Idempotently install the DllImport resolver. Must run before the first SQLite
    /// P/Invoke (i.e. before <c>SqliteConnection</c>'s static ctor); <c>OpenCodeDb</c>'s
    /// static ctor calls this.
    /// </summary>
    public static void Register() {
        if (Interlocked.Exchange(ref _registered, 1) == 1) return;
        // provider.e_sqlite3 declares [DllImport("e_sqlite3")]; the resolver must be
        // registered on THAT assembly. SQLite3Provider_e_sqlite3 is public there and is
        // already rooted by the bundle initializer, so it survives trimming.
        var providerAsm = typeof(SQLitePCL.SQLite3Provider_e_sqlite3).Assembly;
        NativeLibrary.SetDllImportResolver(providerAsm, Resolve);
    }

    static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath) {
        if (!string.Equals(libraryName, LibraryName, StringComparison.Ordinal))
            return IntPtr.Zero; // not ours — let the default resolver handle it

        // 1. Default OS resolution: lib shipped next to the binary (dev/standalone) or
        //    installed system-wide. NativeLibrary.TryLoad does NOT re-enter this resolver.
        if (NativeLibrary.TryLoad(libraryName, assembly, searchPath, out var handle))
            return handle;

        // 2. Download-on-demand into the user cache, then load.
        var path = EnsureNativeLibrary(
            CurrentRid(),
            Environment.GetEnvironmentVariable("KCAP_SQLITE_NATIVE_BASE_URL"),
            DefaultCacheRoot(),
            SelfVersion());
        return NativeLibrary.Load(path);
    }

    internal static string DefaultCacheRoot() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                     ".cache", "kcap", "native", LibraryName);

    /// <summary>
    /// Ensure the verified native library exists in the cache and return its path,
    /// downloading + integrity-checking it if missing. Pure relative to its arguments so
    /// it can be unit-tested against a local mirror and a temp cache root.
    /// </summary>
    internal static string EnsureNativeLibrary(string rid, string? baseUrlOverride, string cacheRoot, string selfVersion) {
        if (!Assets.TryGetValue(rid, out var asset))
            throw new DllNotFoundException(
                $"kcap import --opencode needs the SQLite native library, but no prebuilt e_sqlite3 " +
                $"is available for this platform (rid: {rid}).");

        var cacheDir = Path.Combine(cacheRoot, EngineVersion, rid);
        var target   = Path.Combine(cacheDir, asset.FileName);

        if (File.Exists(target) && FileSha256(target) == asset.Sha256)
            return target;

        Directory.CreateDirectory(cacheDir);

        var (bytes, source) = Fetch(asset, baseUrlOverride, selfVersion);

        var actual = Sha256(bytes);
        if (!string.Equals(actual, asset.Sha256, StringComparison.OrdinalIgnoreCase)) {
            throw new DllNotFoundException(
                $"The SQLite native library fetched from {source} failed its integrity check " +
                $"(expected sha256 {asset.Sha256}, got {actual}). Refusing to load it.");
        }

        // Atomic publish into the cache: write a unique temp file then move into place,
        // tolerating a concurrent process that already won the race.
        var tmp = $"{target}.{Environment.ProcessId}.tmp";
        File.WriteAllBytes(tmp, bytes);
        try {
            File.Move(tmp, target, overwrite: true);
        } catch (IOException) when (File.Exists(target)) {
            try { File.Delete(tmp); } catch { /* best effort */ }
        }
        return target;
    }

    static (byte[] bytes, string source) Fetch(NativeAsset asset, string? baseUrlOverride, string selfVersion) {
        var baseUrl = string.IsNullOrWhiteSpace(baseUrlOverride)
            ? $"https://github.com/kurrent-io/kcap-cli/releases/download/v{selfVersion}"
            : baseUrlOverride.TrimEnd('/', '\\');

        // Local mirror (airgap): a plain directory path or file:// URL.
        if (!baseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) {
            var dir = baseUrl.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
                ? new Uri(baseUrl).LocalPath : baseUrl;
            var local = Path.Combine(dir, asset.AssetName);
            try {
                return (File.ReadAllBytes(local), local);
            } catch (Exception ex) {
                throw NotAvailable(asset, local, ex);
            }
        }

        var url = $"{baseUrl}/{asset.AssetName}";
        try {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"kcap/{selfVersion}");
            return (http.GetByteArrayAsync(url).GetAwaiter().GetResult(), url);
        } catch (Exception ex) {
            throw NotAvailable(asset, url, ex);
        }
    }

    static DllNotFoundException NotAvailable(NativeAsset asset, string source, Exception inner) =>
        new($"kcap import --opencode needs the SQLite native library and tried to fetch it from " +
            $"{source}, but that failed: {inner.Message}. If you are offline or behind a proxy, " +
            $"download {asset.AssetName} into {Path.Combine(DefaultCacheRoot(), EngineVersion, "<rid>")} " +
            $"(renamed to {asset.FileName}), or set KCAP_SQLITE_NATIVE_BASE_URL to an internal mirror " +
            $"(an HTTP base URL or a local directory).", inner);

    /// <summary>Runtime RID, normalized to one of <see cref="Assets"/>' keys.</summary>
    internal static string CurrentRid() {
        var rid = RuntimeInformation.RuntimeIdentifier;
        if (Assets.ContainsKey(rid)) return rid;

        // Some runtimes report a versioned/portable RID (e.g. "osx.15-arm64"); fall back to
        // a canonical os-arch[-musl] form.
        var arch = RuntimeInformation.ProcessArchitecture switch {
            Architecture.Arm64 => "arm64",
            Architecture.X64   => "x64",
            var other          => other.ToString().ToLowerInvariant(),
        };
        if (OperatingSystem.IsWindows()) return $"win-{arch}";
        if (OperatingSystem.IsMacOS())   return $"osx-{arch}";
        return IsMusl() ? $"linux-musl-{arch}" : $"linux-{arch}";
    }

    static bool IsMusl() {
        try {
            return Directory.Exists("/etc/apk")
                || File.Exists("/lib/ld-musl-x86_64.so.1")
                || File.Exists("/lib/ld-musl-aarch64.so.1");
        } catch { return false; }
    }

    internal static string SelfVersion() {
        var info = typeof(SqliteNativeResolver).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
        var plus = info.IndexOf('+');
        return plus >= 0 ? info[..plus] : info;
    }

    static string FileSha256(string path) {
        using var s = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
    }

    static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
