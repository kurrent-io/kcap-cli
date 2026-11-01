namespace Capacitor.Cli.Core;

/// <summary>Why <see cref="AgentsSkillsInstaller.TryInstall"/> could not put the skills in place.</summary>
/// <param name="PluginIncomplete">The skills kcap ships are missing from its own plugin, which a reinstall of kcap
/// puts back; otherwise the target could not be written.</param>
public sealed record SkillsInstallFailure(string Reason, bool PluginIncomplete);
