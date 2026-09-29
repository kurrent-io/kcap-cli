using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Commands;

/// <summary>
/// The arguments the setup Import step pins into its embedded <see cref="ImportCommand.HandleImport"/>
/// call. A record (not a bare argument list) so a test can capture and assert on it through
/// <see cref="ISetupImportRunner"/> without running a real import.
/// </summary>
public sealed record ImportInvocation(
    ImportScope                  Scope,
    int?                         MaxSessions,
    (string Owner, string Name)? CurrentRepo,
    string?                      DefaultVisibility,
    bool                         AutoSkipExclusions,
    bool                         ForcePrivate,
    bool                         SkipTitle,
    ProfileContext               Profiles);
