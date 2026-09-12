namespace Capacitor.Cli.Commands;

/// <summary>
/// Runs the Import step's embedded <c>kcap import</c>. Injected so a test can assert what the step
/// asked for without running an import, and so the arguments the step pins have exactly one reader.
/// </summary>
public interface ISetupImportRunner {
    Task<int> RunAsync(ImportInvocation invocation);
}
