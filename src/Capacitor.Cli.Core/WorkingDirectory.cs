namespace Capacitor.Cli.Core;

/// <summary>
/// The checkout this process acts on, resolved at an entry point and passed down. The single
/// working-directory resolution in the codebase, so a module never depends on where the process
/// happens to be standing — it acts on a checkout because its composer named one.
///
/// <para>Anything shelling out to git passes <see cref="Path"/> as the child's working directory:
/// a child inherits the process cwd otherwise, which puts the ambient value back in the answer.</para>
/// </summary>
public sealed class WorkingDirectory(string path) {
    /// <summary>The directory itself. Not guaranteed to exist.</summary>
    public string Path { get; } = path;

    /// <summary>This process's working directory. Call once, in <c>Main</c> or the composition root.</summary>
    public static WorkingDirectory FromProcess() =>
#pragma warning disable RS0030 // the cwd resolution the ban points every other site at
        new(Directory.GetCurrentDirectory());
#pragma warning restore RS0030
}
