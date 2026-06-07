using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Core;

/// <summary>
/// Decides whether to keep provider API keys (<c>ANTHROPIC_API_KEY</c>,
/// <c>OPENAI_API_KEY</c>) in the spawn environment for headless agent CLIs.
///
/// <para>
/// Default behaviour scrubs them so subscription auth (claude.ai login,
/// ChatGPT account login) is used by <c>claude -p</c> / <c>codex exec</c>;
/// a globally-set key would otherwise override subscription auth and break
/// title generation / summaries (AI-755).
/// </para>
///
/// <para>
/// Users on PAYG / API-key auth opt in via <c>use_provider_api_key: true</c>
/// on their active profile, or via <c>KCAP_USE_PROVIDER_API_KEY=1</c> as a
/// runtime escape hatch. The env var wins when set to a recognised value.
/// </para>
/// </summary>
public static class ProviderApiKeyPolicy {
    public const string EnvVarName = "KCAP_USE_PROVIDER_API_KEY";

    /// <summary>
    /// Convenience entry point: reads the env var and the active profile.
    /// </summary>
    public static bool ShouldKeepProviderKey() => ShouldKeepProviderKey(
        Environment.GetEnvironmentVariable(EnvVarName),
        AppConfig.ResolvedProfile?.Profile);

    /// <summary>
    /// Pure resolver — exposed for testing.
    /// </summary>
    public static bool ShouldKeepProviderKey(string? envValue, Profile? profile) {
        if (TryParseEnv(envValue) is { } envOverride) {
            return envOverride;
        }

        return profile?.UseProviderApiKey ?? false;
    }

    static bool? TryParseEnv(string? value) {
        if (string.IsNullOrWhiteSpace(value)) return null;

        return value.Trim().ToLowerInvariant() switch {
            "1" or "true"  or "yes" or "on"  => true,
            "0" or "false" or "no"  or "off" => false,
            _                                => null
        };
    }
}
