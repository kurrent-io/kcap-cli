namespace Capacitor.Cli.Core.Config;

/// Thrown by <see cref="ConfigMutator.MutateStrict"/> for a config.json that exists but cannot be
/// read: deciding a mutation against the fresh default <see cref="ConfigMutator.TryLoadPure"/> hands
/// back would publish that default over the user's file.
public sealed class ConfigUnreadableException(string configPath)
    : IOException($"The configuration file at {configPath} exists but could not be read.") {
    public string ConfigPath { get; } = configPath;
}
