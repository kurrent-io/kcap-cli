namespace Capacitor.Cli.Core.Accounts;

public sealed record WiringOptions(
    string?                      PluginDir,
    string                       AgentsSkillsDir,
    Func<string?>?               ResolveMcpBinaryPath,
    IReadOnlyCollection<string>? NetworkAllowDomains);
