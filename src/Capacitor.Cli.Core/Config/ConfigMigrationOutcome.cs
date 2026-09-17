namespace Capacitor.Cli.Core.Config;

/// <summary>
/// Whether a config could be understood at all. <see cref="Unreadable"/> means its contents are
/// unknown, not empty — a distinction only a caller deciding on the ABSENCE of a value needs,
/// since <see cref="ConfigMigration.MigrateIfNeeded"/> answers both with a fresh default.
/// </summary>
public enum ConfigMigrationOutcome { Ok, Unreadable }
