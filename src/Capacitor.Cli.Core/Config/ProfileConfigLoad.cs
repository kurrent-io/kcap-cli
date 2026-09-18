namespace Capacitor.Cli.Core.Config;

/// <summary>
/// Why a profile config load ended where it did. <see cref="Missing"/> is a real answer — nothing
/// is configured — while <see cref="Unreadable"/> means the file exists and could not be read or
/// understood, so its contents are unknown rather than empty.
/// </summary>
public enum ProfileConfigLoad { Ok, Missing, Unreadable }
