using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Core.Tests.Unit.LocalIpc;

public class FrameCodecAccountsTests {
    const int LengthPrefix = 4;

    static async Task<LocalFrame> RoundTrip(LocalFrame f) {
        using var ms = new MemoryStream();
        await FrameCodec.WriteAsync(ms, f, default);
        ms.Position = 0;
        return (await FrameCodec.ReadAsync(ms, default))!;
    }

    static LocalFrame SpawnFrame(AgentStartTitle? title = null, string? accountId = null)
        => FrameCodec.Spawn("claude", WorkLocation.BorrowedCwd, false, "/repo", ["fix it"], 80, 24, title, accountId);

    [Test]
    public async Task Spawn_round_trips_an_account_id() {
        var read = await RoundTrip(SpawnFrame(new AgentStartTitle("Fix it", Derived: false), accountId: "abc"));

        var (_, _, _, _, args, _, _, title, accountId) = FrameCodec.Spawn(read);

        await Assert.That(accountId).IsEqualTo("abc");
        await Assert.That(args).IsEquivalentTo(new[] { "fix it" });
        await Assert.That(title).IsEqualTo(new AgentStartTitle("Fix it", Derived: false));
    }

    [Test]
    public async Task Spawn_with_account_but_no_title_round_trips() {
        var (_, _, _, _, _, _, _, title, accountId) = FrameCodec.Spawn(await RoundTrip(SpawnFrame(accountId: "abc")));

        await Assert.That(title).IsNull();
        await Assert.That(accountId).IsEqualTo("abc");
    }

    [Test]
    public async Task Spawn_without_an_account_parses_null() {
        var (_, _, _, _, _, _, _, _, accountId) = FrameCodec.Spawn(await RoundTrip(SpawnFrame()));

        await Assert.That(accountId).IsNull();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Spawn_without_account_bytes_parses_null(bool withTitle) {
        // A frame from a CLI that predates the account ends right after the title.
        var title    = withTitle ? new AgentStartTitle("Fix it", Derived: true) : (AgentStartTitle?)null;
        var complete = SpawnFrame(title).Bytes;
        var older    = new LocalFrame(FrameType.Spawn) { Bytes = complete[..^LengthPrefix] };

        var (_, _, _, _, args, _, _, parsedTitle, accountId) = FrameCodec.Spawn(older);

        await Assert.That(accountId).IsNull();
        await Assert.That(parsedTitle).IsEqualTo(title);
        await Assert.That(args).IsEquivalentTo(new[] { "fix it" });
    }

    [Test]
    public async Task Spawn_account_rides_after_every_field_an_older_daemon_reads() {
        var plain       = SpawnFrame(accountId: null).Bytes;
        var withAccount = SpawnFrame(accountId: "abc").Bytes;

        var olderLayout = plain[..^LengthPrefix];
        await Assert.That(withAccount.Length).IsGreaterThan(plain.Length);
        await Assert.That(withAccount[..olderLayout.Length]).IsEquivalentTo(olderLayout);
    }

    [Test]
    public async Task Spawn_with_a_truncated_account_is_a_protocol_error() {
        var bytes = SpawnFrame(accountId: "abc").Bytes;
        var frame = new LocalFrame(FrameType.Spawn) { Bytes = bytes[..^1] };

        await Assert.That(() => FrameCodec.Spawn(frame)).Throws<InvalidDataException>();
    }

    [Test]
    public async Task AccountsSnapshot_frame_carries_the_json_as_text() {
        const string json = """{"host_id":"h","revision":3,"accounts":[],"defaults":[]}""";

        var read = await RoundTrip(FrameCodec.AccountsSnapshot(json));

        await Assert.That(read.Type).IsEqualTo(FrameType.AccountsSnapshot);
        await Assert.That(read.Text).IsEqualTo(json);
    }

    [Test]
    public async Task AccountsSubscribe_round_trips_with_an_empty_payload() {
        var read = await RoundTrip(LocalFrame.AccountsSubscribe());

        await Assert.That(read.Type).IsEqualTo(FrameType.AccountsSubscribe);
        await Assert.That(read.Text).IsEmpty();
        await Assert.That(read.Bytes).IsEmpty();
    }

    [Test]
    public async Task Frame_values_are_pinned_32_and_87() {
#pragma warning disable TUnitAssertions0005
        await Assert.That((byte)FrameType.AccountsSubscribe).IsEqualTo((byte)32);
        await Assert.That((byte)FrameType.AccountsSnapshot).IsEqualTo((byte)87);
#pragma warning restore TUnitAssertions0005
    }
}
