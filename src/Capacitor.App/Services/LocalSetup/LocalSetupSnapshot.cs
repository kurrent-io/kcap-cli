namespace Capacitor.App.Services.LocalSetup;

/// The local setup's durable state as last read from the journal, config and registry.
public readonly record struct LocalSetupSnapshot(bool Pending, bool CommittedLocal);
