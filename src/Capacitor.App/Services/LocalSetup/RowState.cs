namespace Capacitor.App.Services.LocalSetup;

/// Skipped ends a step that did not run without failing the setup.
public enum RowState { Pending, Running, Done, Skipped }
