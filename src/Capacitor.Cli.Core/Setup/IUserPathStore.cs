namespace Capacitor.Cli.Core.Setup;

/// The user-scope PATH as stored, unexpanded — what a new terminal is built from.
public interface IUserPathStore {
    string? Read();

    /// Appends one directory, keeping every existing entry byte for byte.
    void Append(string directory);
}
