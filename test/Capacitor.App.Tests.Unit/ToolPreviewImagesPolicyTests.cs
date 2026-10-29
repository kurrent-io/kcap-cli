using System.Text;
using Avalonia.Svg.Skia;
using Capacitor.App.Services;
using SkiaSharp;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.App.Tests.Unit;

[NotInParallel("AvaloniaSession")]
public class ToolPreviewImagesPolicyTests {
    const string Shape = "<rect id='background' width='16' height='16' fill='red'/>";
    const string ExternalSvg = """
        <svg xmlns="http://www.w3.org/2000/svg" width="16" height="16">
          <rect id="shape" width="16" height="16" fill="lime"/>
        </svg>
        """;
    const string Css = "#background { fill: lime; }";

    [Test]
    [Arguments("image")]
    [Arguments("use")]
    [Arguments("nested_svg")]
    public async Task External_resources_are_blocked(string kind) {
        using var tmp = new TempDir();
        var png = GreenPng();
        var imagePath = tmp.CreateFile("asset.png");
        File.WriteAllBytes(imagePath, png);
        tmp.CreateFile("asset.svg", ExternalSvg);
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/asset.png")).RespondWith(
            Response.Create().WithHeader("Content-Type", "image/png").WithBody(png));
        server.Given(Request.Create().WithPath("/asset.svg")).RespondWith(
            Response.Create().WithHeader("Content-Type", "image/svg+xml").WithBody(ExternalSvg));

        await AvaloniaSession.RunOnUiAsync(async () => {
            try {
                var fileRoot = new Uri(tmp.Path + "/").AbsoluteUri;
                foreach (var root in new[] { fileRoot, server.Url + "/" }) {
                    var svg = ResourceDocument(kind, root);
                    // A working unrestricted control prevents unsupported syntax from passing the isolation check.
                    using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(svg)))
                    using (var unrestricted = await Task.Run(() => SvgSource.LoadFromStream(stream))) {
                        await Assert.That(Pixel(unrestricted)).IsEqualTo(SKColors.Lime);
                    }
                    if (root.StartsWith("http", StringComparison.Ordinal))
                        await Assert.That(server.LogEntries.Count > 0).IsTrue();
                    server.ResetLogEntries();
                    SKColor missingColor;
                    using (var missing = new MemoryStream(Encoding.UTF8.GetBytes(ResourceDocument(kind, new Uri(tmp.PathTo("missing") + "/").AbsoluteUri))))
                    using (var missingSource = await Task.Run(() => SvgSource.LoadFromStream(missing)))
                        missingColor = Pixel(missingSource);
                    var path = tmp.CreateFile(Guid.NewGuid() + ".svg", svg);
                    using var preview = await Task.Run(() => ToolPreviewImages.Acquire(path));
                    await Assert.That(preview).IsNotNull();
                    var image = (SvgImage)preview!.Image;
                    await Assert.That(Pixel(image.Source!)).IsEqualTo(missingColor);
                    await Assert.That(Pixel(image.Source!)).IsNotEqualTo(SKColors.Lime);
                    await Assert.That(server.LogEntries).IsEmpty();
                }
            } finally {
                ToolPreviewImages.Clear();
            }
        });
    }

    [Test]
    public async Task Same_document_data_resources_and_bitmap_previews_still_render() {
        using var tmp = new TempDir();
        var png = GreenPng();
        var pngPath = tmp.CreateFile("bitmap.png");
        File.WriteAllBytes(pngPath, png);
        var dataPng = "data:image/png;base64," + Convert.ToBase64String(png);
        var local = "<defs><rect id='local' width='16' height='16' fill='lime'/></defs><use href='#local'/>";
        var dataCss = "<style>@import url('data:text/css;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(Css)) + "');</style>";
        await AvaloniaSession.RunOnUiAsync(async () => {
            try {
                foreach (var body in new[] { local, Image(dataPng), Image(DataSvg(Document(Image(dataPng)))), dataCss }) {
                    var path = tmp.CreateFile(Guid.NewGuid() + ".svg", Document(body));
                    using var preview = await Task.Run(() => ToolPreviewImages.Acquire(path));
                    await Assert.That(preview).IsNotNull();
                    await Assert.That(Pixel(((SvgImage)preview!.Image).Source!)).IsEqualTo(SKColors.Lime);
                }
                using var bitmap = await Task.Run(() => ToolPreviewImages.Acquire(pngPath));
                await Assert.That(bitmap).IsNotNull();
                await Assert.That(bitmap!.Image is Avalonia.Media.Imaging.Bitmap).IsTrue();
                await Assert.That(bitmap.Image.Size.Width > 0).IsTrue();
            } finally {
                ToolPreviewImages.Clear();
            }
        });
    }

    static string ResourceDocument(string kind, string root) => kind switch {
        "image" => Document(Image(root + "asset.png")),
        "use" => Document($"<use href='{root}asset.svg#shape'/>"),
        "nested_svg" => Document(Image(DataSvg(Document(Image(root + "asset.png"))))),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    static string Document(string body) =>
        "<svg xmlns='http://www.w3.org/2000/svg' xmlns:xlink='http://www.w3.org/1999/xlink' width='16' height='16'>" + Shape + body + "</svg>";

    static string Image(string href) => $"<image href='{href}' width='16' height='16'/>";

    static string DataSvg(string svg) => "data:image/svg+xml;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(svg));

    static byte[] GreenPng() {
        using var bitmap = new SKBitmap(16, 16);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Lime);
        using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    static SKColor Pixel(SvgSource source) {
        using var bitmap = new SKBitmap(16, 16);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        canvas.DrawPicture(source.Picture!);
        return bitmap.GetPixel(8, 8);
    }
}
