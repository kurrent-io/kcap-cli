namespace Capacitor.App.Services.Mutation;

/// A missing configured override refuses rather than falling through to a different executable.
public readonly record struct CliPin(string? Path, bool OverrideMissing = false) {
    public static implicit operator CliPin(string? path) => new(path);
}
