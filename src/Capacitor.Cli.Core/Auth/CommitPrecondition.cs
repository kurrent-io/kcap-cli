using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Core.Auth;

/// What must still hold about the target profile when the commit boundary writes config. Checked
/// inside the strict config mutation, so a change made while the browser was open is refused
/// rather than committed over.
public abstract record CommitPrecondition {
    internal abstract void Check(ProfileConfig config, string profile);

    /// The profile exists and still names <paramref name="Url"/>.
    public sealed record ExpectServer(string Url) : CommitPrecondition {
        internal override void Check(ProfileConfig config, string profile) {
            if (!config.Profiles.TryGetValue(profile, out var existing))
                throw new CommitPreconditionFailedException($"profile '{profile}' was removed during sign-in; nothing saved.");
            if (!ServerIdentity.SameServer(existing.ServerUrl, Url))
                throw new CommitPreconditionFailedException($"profile '{profile}' does not name {Url}; nothing saved.");
        }
    }

    /// Every profile with a server names <paramref name="Url"/>: a local setup that repoints the
    /// active profile must not leave another profile's credential behind a different server.
    /// <paramref name="Canonicalize"/> rewrites each profile's URL before the comparison, so another
    /// spelling of the local installation's own origin counts as the same server.
    public sealed record OnlyServer(string Url, Func<string?, string?>? Canonicalize = null) : CommitPrecondition {
        internal override void Check(ProfileConfig config, string profile) {
            foreach (var (name, other) in config.Profiles) {
                if (string.IsNullOrWhiteSpace(other.ServerUrl)) continue;
                if (ServerIdentity.SameServer(Canonicalize is null ? other.ServerUrl : Canonicalize(other.ServerUrl), Url)) continue;

                throw new CommitPreconditionFailedException(
                    $"profile '{name}' points at {other.ServerUrl}; local setup needs every profile on {Url}; nothing saved.");
            }
        }
    }
}
