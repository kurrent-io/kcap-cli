using System.Net;
using Capacitor.Cli.Core.Http;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Core.Tests.Unit.Http;

/// <summary>
/// <see cref="ArtefactsApi"/> driven against a stub server. What is under test is the status
/// mapping, because that is where the client can quietly lose information the server went out of
/// its way to keep: a 403 and a 404 mean different things (visible-but-not-yours vs. no such
/// artefact), and a refusal carries the ceiling it hit.
/// </summary>
public class ArtefactsApiTests : IDisposable {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    readonly WireMockServer _server = WireMockServer.Start();

    string Url => _server.Urls[0];

    public void Dispose() => _server.Stop();

    ArtefactsApi Api() {
        var profiles = Resolutions.At(Url, Config.Root);

        return new ArtefactsApi(new FixedCapacitorHttpClient(), new CapacitorServer(Url, Config.Root, profiles));
    }

    static PublishArtefactBody Body() => new("Plan", "<p>x</p>", null, null, null, null);

    const string DetailJson = """
        {
          "artefact": {
            "artefact_id": "a1",
            "title": "Plan",
            "owner_user_id": "github:7",
            "visibility": "org",
            "latest_version": 2,
            "updated_at": "2026-09-16T10:00:00+00:00",
            "is_owner": true,
            "url": "https://kcap.test/artefacts/a1"
          },
          "grants": [{ "grant_type": "team", "grantee_id": "platform", "grantee_name": "Platform" }]
        }
        """;

    const string ListJson = """
        {
          "artefacts": [{
            "artefact_id": "a1",
            "title": "Plan",
            "owner_user_id": "github:7",
            "visibility": "org",
            "latest_version": 2,
            "updated_at": "2026-09-16T10:00:00+00:00",
            "is_owner": true,
            "url": "https://kcap.test/artefacts/a1"
          }]
        }
        """;

    [Test]
    public async Task A_created_artefact_comes_back_whole() {
        _server.Given(Request.Create().WithPath("/api/artefacts").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(HttpStatusCode.Created)
                                    .WithHeader("Content-Type", "application/json").WithBody(DetailJson));

        var result = await Api().PublishAsync(Body());

        var written = await Assert.That(result).IsTypeOf<ArtefactWriteResult.Written>();

        // snake_case is the server's global policy — binding it with web defaults would leave every
        // one of these silently at its default.
        await Assert.That(written!.Detail.Artefact.ArtefactId).IsEqualTo("a1");
        await Assert.That(written.Detail.Artefact.LatestVersion).IsEqualTo(2);
        await Assert.That(written.Detail.Artefact.IsOwner).IsTrue();
        await Assert.That(written.Detail.Artefact.Url).IsEqualTo("https://kcap.test/artefacts/a1");
        await Assert.That(written.Detail.Grants![0].GranteeName).IsEqualTo("Platform");
    }

    [Test]
    public async Task A_refusal_carries_the_limit_it_hit() {
        _server.Given(Request.Create().WithPath("/api/artefacts").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(HttpStatusCode.BadRequest)
                                    .WithHeader("Content-Type", "application/json")
                                    .WithBody("""{"code":"content_too_large","message":"Too big.","limit":4194304}"""));

        var refused = await Assert.That(await Api().PublishAsync(Body())).IsTypeOf<ArtefactWriteResult.Refused>();

        await Assert.That(refused!.Error.Code).IsEqualTo("content_too_large");
        await Assert.That(refused.Error.Limit).IsEqualTo(4194304L);
    }

    [Test]
    public async Task Forbidden_and_not_found_stay_apart() {
        // The server answers 404 when the caller could not have seen the artefact at all and 403
        // when they can see it but do not own it. Collapsing them here would throw away the only
        // signal that tells someone which of the two happened.
        _server.Given(Request.Create().WithPath("/api/artefacts/a1/visibility").UsingPut())
               .RespondWith(Response.Create().WithStatusCode(HttpStatusCode.Forbidden));

        _server.Given(Request.Create().WithPath("/api/artefacts/gone/visibility").UsingPut())
               .RespondWith(Response.Create().WithStatusCode(HttpStatusCode.NotFound));

        await Assert.That(await Api().SetVisibilityAsync("a1", "org", null)).IsTypeOf<ArtefactWriteResult.NotYours>();
        await Assert.That(await Api().SetVisibilityAsync("gone", "org", null)).IsTypeOf<ArtefactWriteResult.NotFound>();
    }

    [Test]
    public async Task A_delete_answers_no_content() {
        _server.Given(Request.Create().WithPath("/api/artefacts/a1").UsingDelete())
               .RespondWith(Response.Create().WithStatusCode(HttpStatusCode.NoContent));

        await Assert.That(await Api().DeleteAsync("a1")).IsTypeOf<ArtefactWriteResult.Gone>();
    }

    [Test]
    public async Task An_id_is_escaped_into_its_own_path_segment() {
        // The stub matches the DECODED path, so this passing is what proves the id was escaped into
        // one segment on the wire rather than left to split the route.
        _server.Given(Request.Create().WithPath("/api/artefacts/a b/versions").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(HttpStatusCode.OK)
                                    .WithHeader("Content-Type", "application/json").WithBody(DetailJson));

        await Assert.That(await Api().PublishVersionAsync("a b", "<p>x</p>")).IsTypeOf<ArtefactWriteResult.Written>();
    }

    [Test]
    public async Task A_listing_binds_its_snake_case_envelope() {
        _server.Given(Request.Create().WithPath("/api/artefacts").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(HttpStatusCode.OK)
                                    .WithHeader("Content-Type", "application/json")
                                    .WithBody(ListJson));

        var listed = await Api().ListAsync();

        await Assert.That(listed.Count).IsEqualTo(1);
        await Assert.That(listed[0].ArtefactId).IsEqualTo("a1");
        await Assert.That(listed[0].IsOwner).IsTrue();
    }

    [Test]
    public async Task An_unexpected_status_is_an_exception_rather_than_a_silent_value() {
        _server.Given(Request.Create().WithPath("/api/artefacts").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(HttpStatusCode.InternalServerError).WithBody("boom"));

        await Assert.That(async () => await Api().PublishAsync(Body())).Throws<CapacitorApiException>();
    }
}
