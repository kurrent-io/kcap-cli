using Capacitor.Cli.Core.Harness.Grok;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Grok;

public class GrokPathsTests {
    [TempDir] public required TempDir Tmp { get; init; }

    [Test]
    public async Task Defaults_under_dot_grok() {
        var home = Path.Combine("x", "home");
        var p    = new GrokPaths(new UserHome(home), null);

        await Assert.That(p.Home).IsEqualTo(Path.Combine(home, ".grok"));
        await Assert.That(p.KcapHooksJson).IsEqualTo(Path.Combine(home, ".grok", "hooks", "kcap.json"));
        await Assert.That(p.SessionsDir).IsEqualTo(Path.Combine(home, ".grok", "sessions"));
    }

    [Test]
    public async Task Grok_home_replaces_the_root() {
        var custom = Path.Combine("custom", "g");

        await Assert.That(new GrokPaths(new UserHome("h"), custom).Home).IsEqualTo(custom);
    }

    /// <summary>The directory name is the cwd percent-encoded the way Grok writes it: <c>/</c> as
    /// <c>%2F</c>, <c>-</c> left alone.</summary>
    [Test]
    public async Task A_session_s_transcript_sits_under_the_percent_encoded_cwd() {
        var p = new GrokPaths(new UserHome(Tmp.Path), null);

        var path = p.UpdatesJsonl("/Users/me/dev/field-technical-services", "01a1359d-bed2-7ce1-bad9-a99e91d9994e");

        await Assert.That(path).IsEqualTo(Path.Combine(
            p.SessionsDir, "%2FUsers%2Fme%2Fdev%2Ffield-technical-services", "01a1359d-bed2-7ce1-bad9-a99e91d9994e", "updates.jsonl"));
    }

    /// <summary>Grok swaps a name over 255 bytes for a slug and records the real cwd in a <c>.cwd</c>
    /// file beside the session directories.</summary>
    [Test]
    public async Task A_cwd_too_long_to_encode_resolves_through_its_dot_cwd_file() {
        var p   = new GrokPaths(new UserHome(Tmp.Path), null);
        var cwd = "/" + string.Join("/", Enumerable.Repeat("a-very-long-directory-name", 12));
        var dir = Tmp.CreateDir(".grok", "sessions", "a-very-long-slug-3f2a9c");
        Tmp.CreateFile(Path.Combine(".grok", "sessions", "a-very-long-slug-3f2a9c", ".cwd"), cwd + "\n");

        await Assert.That(p.UpdatesJsonl(cwd, "s1")).IsEqualTo(Path.Combine(dir.Path, "s1", "updates.jsonl"));
    }

    /// <summary>A cwd may end in a space; only the file's line ending is not part of it.</summary>
    [Test]
    public async Task A_long_cwd_ending_in_a_space_still_matches_its_dot_cwd_file() {
        var p   = new GrokPaths(new UserHome(Tmp.Path), null);
        var cwd = "/" + string.Join("/", Enumerable.Repeat("a-very-long-directory-name", 12)) + " ";
        var dir = Tmp.CreateDir(".grok", "sessions", "slug-with-space");
        Tmp.CreateFile(Path.Combine(".grok", "sessions", "slug-with-space", ".cwd"), cwd + "\n");

        await Assert.That(p.UpdatesJsonl(cwd, "s1")).IsEqualTo(Path.Combine(dir.Path, "s1", "updates.jsonl"));
    }

    [Test]
    public async Task A_long_cwd_with_no_matching_dot_cwd_file_has_no_transcript_path() {
        var p   = new GrokPaths(new UserHome(Tmp.Path), null);
        var cwd = "/" + string.Join("/", Enumerable.Repeat("a-very-long-directory-name", 12));

        await Assert.That(p.UpdatesJsonl(cwd, "s1")).IsNull();
    }

    [Test]
    public async Task Has_user_data_needs_a_session_or_grok_s_own_config() {
        var p = new GrokPaths(new UserHome(Tmp.Path), null);
        await Assert.That(p.HasUserData()).IsFalse();

        Tmp.CreateDir(".grok", "hooks");
        await Assert.That(p.HasUserData()).IsFalse();          // kcap can create hooks/ before Grok runs

        Tmp.CreateFile(Path.Combine(".grok", "config.toml"), "");
        await Assert.That(p.HasUserData()).IsTrue();
    }
}
