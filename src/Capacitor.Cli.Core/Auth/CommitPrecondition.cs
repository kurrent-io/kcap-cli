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
}
