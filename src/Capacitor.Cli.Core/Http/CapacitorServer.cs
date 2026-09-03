using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Core.Http;

/// <summary>
/// The one Capacitor server this process talks to, plus the context reaching it needs. A handler the
/// factory constructs cannot be handed per-call arguments, so this carries them as a singleton.
/// </summary>
public sealed class CapacitorServer(string url, ConfigRoot config, ProfileContext profiles) {
    public string         Url      { get; } = url;
    public ConfigRoot     Config   { get; } = config;
    public ProfileContext Profiles { get; } = profiles;
}
