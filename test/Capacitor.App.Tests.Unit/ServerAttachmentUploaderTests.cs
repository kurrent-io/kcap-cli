using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Http;
using Microsoft.Extensions.DependencyInjection;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.App.Tests.Unit;

public class ServerAttachmentUploaderTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    const string IdA = "a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1";
    const string IdB = "b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2";
    const string IdC = "c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3";

    static IReadOnlyList<StagedAttachment> TwoFiles() => [
        new StagedAttachment("a.png", "image/png", new byte[] { 1, 2, 3 }),
        new StagedAttachment("b.txt", "text/plain", new byte[] { 4, 5 }),
    ];

    [Test]
    public async Task Posts_one_multipart_part_per_file_with_name_type_and_filename_and_returns_ids_in_order() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/attachments/upload").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                .WithBody($$"""[{"id":"{{IdA}}","fileName":"a.png","size":3},{"id":"{{IdB}}","fileName":"b.txt","size":2}]"""));
        var (http, profiles, provider) = await WireMockLane.BuildAsync(server, Config.Root);
        await using var _ = provider;
        var uploader = new ServerAttachmentUploader(http, profiles);

        var outcome = await uploader.UploadAsync(TwoFiles(), CancellationToken.None);

        await Assert.That(outcome.Kind).IsEqualTo(UploadKind.Uploaded);
        await Assert.That(outcome.Ids).IsEquivalentTo([IdA, IdB]);

        var request = server.LogEntries.Single(e => e.RequestMessage.Path == "/api/attachments/upload").RequestMessage;
        await Assert.That(request.Headers!["Content-Type"].Single()).StartsWith("multipart/form-data");
        var body = request.Body!;
        await Assert.That(body).Contains("name=files");
        await Assert.That(body).Contains("filename=a.png");
        await Assert.That(body).Contains("Content-Type: image/png");
        await Assert.That(body).Contains("filename=b.txt");
        await Assert.That(body).Contains("Content-Type: text/plain");
    }

    [Test]
    [Arguments("[]")]
    [Arguments("[{\"id\":\"" + IdA + "\"}]")]
    [Arguments("[{\"id\":\"" + IdA + "\"},{\"id\":\"" + IdB + "\"},{\"id\":\"" + IdC + "\"}]")]
    [Arguments("[{\"id\":null},{\"id\":\"" + IdB + "\"}]")]
    [Arguments("[{\"id\":\"\"},{\"id\":\"" + IdB + "\"}]")]
    [Arguments("[{\"id\":\"nope\"},{\"id\":\"" + IdB + "\"}]")]
    [Arguments("[{\"id\":\"" + IdA + "\"},{\"id\":\"" + IdA + "\"}]")]
    [Arguments("not json")]
    public async Task A_200_with_the_wrong_shape_is_rejected_with_the_stated_reason(string body) {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/attachments/upload").UsingPost())
              .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(body));
        var (http, profiles, provider) = await WireMockLane.BuildAsync(server, Config.Root);
        await using var _ = provider;
        var uploader = new ServerAttachmentUploader(http, profiles);

        var outcome = await uploader.UploadAsync(TwoFiles(), CancellationToken.None);

        await Assert.That(outcome.Kind).IsEqualTo(UploadKind.Rejected);
        await Assert.That(outcome.Reason).IsEqualTo(ServerAttachmentUploader.UnexpectedResponse);
        await Assert.That(outcome.Ids).IsEmpty();
    }

    [Test]
    public async Task Status_codes_map_to_kinds() {
        var badRequest = await UploadWithStatusAsync(400, "nope");
        await Assert.That(badRequest.Kind).IsEqualTo(UploadKind.Rejected);
        await Assert.That(badRequest.Reason).IsEqualTo("nope");

        var unauthorized = await UploadWithStatusAsync(401, null);
        await Assert.That(unauthorized.Kind).IsEqualTo(UploadKind.Unauthorized);
        await Assert.That(unauthorized.Reason).IsEqualTo("not_signed_in");

        var serverError = await UploadWithStatusAsync(500, null);
        await Assert.That(serverError.Kind).IsEqualTo(UploadKind.Unreachable);
        await Assert.That(serverError.Reason).IsEqualTo("server_status_500");

        const string closedPort = "http://127.0.0.1:1";
        var profiles = Resolutions.At(closedPort, Config.Root);
        await AuthFixtures.NewTokenStore(Config.Root).SaveAsync(profiles.Name, new StoredTokens {
            AccessToken = "tok", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1), GitHubUsername = "alice",
            Provider = AuthProvider.GitHubApp, ServerUrl = closedPort,
        });
        var provider = new ServiceCollection()
            .AddSingleton(Config.Root).AddSingleton(profiles).AddSingleton(new CapacitorServer(closedPort, Config.Root, profiles))
            .AddCapacitorHttp(ProfileOverrides.None, MachineAuth.None).BuildValidated();
        await using var _ = provider;
        var refused = await new ServerAttachmentUploader(provider.GetRequiredService<ICapacitorHttpClient>(), profiles)
            .UploadAsync(TwoFiles(), CancellationToken.None);
        await Assert.That(refused.Kind).IsEqualTo(UploadKind.Unreachable);
    }

    async Task<UploadOutcome> UploadWithStatusAsync(int statusCode, string? body) {
        using var server = WireMockServer.Start();
        var response = Response.Create().WithStatusCode(statusCode);
        if (body is not null) response = response.WithBody(body);
        server.Given(Request.Create().WithPath("/api/attachments/upload").UsingPost()).RespondWith(response);
        var (http, profiles, provider) = await WireMockLane.BuildAsync(server, Config.Root);
        await using var _ = provider;
        return await new ServerAttachmentUploader(http, profiles).UploadAsync(TwoFiles(), CancellationToken.None);
    }

    [Test]
    public async Task Not_signed_in_is_unauthorized_without_a_request() {
        using var server = WireMockServer.Start();
        var profiles = Resolutions.At(server.Url!, Config.Root);
        var http = new RecordingCapacitorHttpClient(status: AuthStatus.NotAuthenticated);
        var uploader = new ServerAttachmentUploader(http, profiles);

        var outcome = await uploader.UploadAsync(TwoFiles(), CancellationToken.None);

        await Assert.That(outcome.Kind).IsEqualTo(UploadKind.Unauthorized);
        await Assert.That(outcome.Reason).IsEqualTo("not_signed_in");
        await Assert.That(server.LogEntries).IsEmpty();
    }
}
