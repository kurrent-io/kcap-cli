using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.Install;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// A script install on disk (one version, <c>current</c> linked to it, the marker) and a stub serving
/// the installer's manifests and a release archive the way kcap-web and GitHub do.
/// </summary>
sealed class ScriptReleaseFixture : IDisposable {
    public const string Rid = "linux-x64";

    readonly TempDir _tmp = new("script");
    readonly WireMockServer _server = WireMockServer.Start();

    public ScriptInstallLayout Layout { get; }

    public ScriptReleaseFixture(string installedVersion = "1.0.0") {
        Layout = new ScriptInstallLayout(_tmp.PathTo("kcap"));
        _tmp.CreateFile($"kcap/versions/{installedVersion}/bin/kcap", "old");
        _tmp.CreateFile("kcap/install.json", """{"source":"script","channel":"latest"}""");
        Directory.CreateSymbolicLink(Layout.Current, $"versions/{installedVersion}");
    }

    public KcapReleaseClient Client() =>
        new(new HttpClient(), TimeProvider.System) { BaseUrl = _server.Urls[0], ReleasesUrl = _server.Urls[0] + "/releases" };

    public IReadOnlyList<string> Requests => [.. _server.LogEntries.Select(e => e.RequestMessage.Path)];

    /// <summary>Publishes <paramref name="version"/>: its archive, and a manifest whose checksum is the
    /// archive's unless <paramref name="manifestSha"/> overrides it.</summary>
    public void Publish(string version, string? manifestSha = null, string archiveRid = Rid, bool channel = true) {
        var archive = Archive(version);
        var sha     = manifestSha ?? Convert.ToHexStringLower(SHA256.HashData(archive));
        var json    = $$$$"""{"version":"{{{{version}}}}","commit":"c","platforms":{"{{{{archiveRid}}}}":{"sha256":"{{{{sha}}}}","size":{{{{archive.Length}}}},"url":"https://ignored.invalid/x"}}}""";

        _server.Given(Request.Create().WithPath($"/download/cli/{version}/manifest.json").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(json));
        if (channel)
            _server.Given(Request.Create().WithPath("/download/cli/channels/latest.json").UsingGet())
                .RespondWith(Response.Create().WithStatusCode(200).WithBody(json));
        _server.Given(Request.Create().WithPath($"/releases/v{version}/kcap-{Rid}.tar.gz").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(archive));
    }

    /// <summary>The release layout: <c>bin/kcap</c>, <c>bin/kcap-daemon</c> and the plugin directory.
    /// Written entry by entry: <c>TarFile.CreateFromDirectory</c> skips dotfiles such as <c>.mcp.json</c>.</summary>
    static byte[] Archive(string version) {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip)) {
            Add(tar, "bin/kcap", $"kcap {version}");
            Add(tar, "bin/kcap-daemon", $"daemon {version}");
            Add(tar, "kcap/.mcp.json", "{}");
        }

        return buffer.ToArray();
    }

    static void Add(TarWriter tar, string name, string content) =>
        tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) {
            DataStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content)),
            Mode       = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
        });

    public string? CurrentTarget => new DirectoryInfo(Layout.Current).LinkTarget;

    public void Dispose() {
        _server.Stop();
        _tmp.Dispose();
    }
}
