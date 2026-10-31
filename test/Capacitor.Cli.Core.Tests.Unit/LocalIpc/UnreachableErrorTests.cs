using System.Net.Sockets;
using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Core.Tests.Unit.LocalIpc;

/// <summary>
/// The "server unreachable" stderr line.
///
/// <para>Two things ride on the sanitization. A <c>server_url</c> may carry userinfo credentials, and
/// this line is reachable from the hook path (<c>AgentHookPoster.PostAsync</c> on any transport fault),
/// so a raw URL would print them on every lifecycle POST for every vendor. And a control
/// character anywhere in the rendered line can fabricate a second line in a stream harnesses parse —
/// Gemini in particular reads hook stderr as the hook's own output when stdout is empty.</para>
/// </summary>
public class UnreachableErrorTests {
    [Test]
    public async Task The_line_drops_credentials_from_the_url() {
        var line = HttpClientExtensions.RenderUnreachableError(
            "https://user:sup3rs3cret@kcap.example.com", "No such host is known.");

        await Assert.That(line).DoesNotContain("sup3rs3cret");
        await Assert.That(line).DoesNotContain("user:");
    }

    /// <summary>The complement, and the reason this cannot be "sanitize to nothing": the whole point of
    /// the line is naming WHICH server was unreachable, so the host and the cause must survive.</summary>
    [Test]
    public async Task The_line_still_names_the_host_and_the_cause() {
        var line = HttpClientExtensions.RenderUnreachableError(
            "https://user:sup3rs3cret@kcap.example.com", "No such host is known.");

        await Assert.That(line).Contains("kcap.example.com");
        await Assert.That(line).Contains("No such host is known.");
    }

    /// <summary>A credential can hide behind a second '@' — take the LAST one, as Sanitize does.</summary>
    [Test]
    [Arguments("https://user:sup3rs3cret@")]
    [Arguments("https://a@b:sup3rs3cret@host")]
    public async Task The_line_drops_credentials_in_the_awkward_shapes_too(string url) {
        await Assert.That(HttpClientExtensions.RenderUnreachableError(url, "boom")).DoesNotContain("sup3rs3cret");
    }

    /// <summary>No variable component may fabricate a line — not the URL, and not the exception message
    /// either. The fixed text holds no control character, so none may appear at all: a lone <c>\r</c>
    /// sends a terminal's cursor back and paints the rest of the line over its start.</summary>
    [Test]
    [Arguments("https://host\r\n[kcap] everything is fine", "boom")]
    [Arguments("https://host", "boom\r\n[kcap] everything is fine")]
    public async Task No_variable_component_can_inject_a_second_line(string url, string message) {
        var line = HttpClientExtensions.RenderUnreachableError(url, message);

        await Assert.That(line.Any(char.IsControl)).IsFalse();
    }

    [Test]
    [Arguments(ConnectionFailure.Refused)]
    [Arguments(ConnectionFailure.NameNotResolved)]
    [Arguments(ConnectionFailure.TimedOut)]
    [Arguments(ConnectionFailure.Other)]
    public async Task Every_classification_reads_with_single_spaces_and_never_suggests_a_sign_in(ConnectionFailure failure) {
        var line = HttpClientExtensions.RenderUnreachableError("http://localhost:5000", " boom ", failure);

        await Assert.That(line).DoesNotContain("  ");
        await Assert.That(line).DoesNotContain("login");
        await Assert.That(line).Contains($"({failure.Reason})");
        await Assert.That(line).Contains(failure.Remedy);
    }

    [Test]
    public async Task A_refused_connection_asks_for_the_server_to_be_started() {
        var line = HttpClientExtensions.RenderUnreachableError("http://localhost:5000", "refused", ConnectionFailure.Refused);

        await Assert.That(line).StartsWith(
            "kcap API cannot be reached at http://localhost:5000 (connection refused). Start or restart the server");
    }

    /// <summary>The retry loop wraps the last transport fault; the outer message alone would hide that
    /// the connection was refused.</summary>
    [Test]
    public async Task The_text_names_the_fault_the_retry_loop_wrapped() {
        var refused = new HttpRequestException(HttpRequestError.ConnectionError, "Connection refused (localhost:5000)",
            new SocketException((int)SocketError.ConnectionRefused));
        var spent = new HttpRequestException("Request failed, and the 30s retry budget ran out.", refused);

        var line = HttpClientExtensions.UnreachableErrorText("http://localhost:5000", spent);

        await Assert.That(line).Contains("(connection refused)");
        await Assert.That(line).EndsWith(
            "Detail: Request failed, and the 30s retry budget ran out. Last error: Connection refused (localhost:5000)");
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments(null)]
    public async Task A_blank_url_renders_without_throwing(string? url) {
        await Assert.That(HttpClientExtensions.RenderUnreachableError(url, "boom")).IsNotEmpty();
    }
}
