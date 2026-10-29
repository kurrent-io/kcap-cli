namespace Capacitor.Cli.Core.Http;

/// <summary>Where the update check reads the newest version on a channel: npm for an npm install, the
/// installer's channel manifest for a script install.</summary>
public interface IReleaseFeed {
    Task<NpmDistTag> GetDistTagAsync(string channel, CancellationToken ct);
}
