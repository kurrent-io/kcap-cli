namespace Capacitor.Cli.Core.LocalIpc;

/// <summary><see cref="Session"/> is non-null exactly when the outcome is <see cref="AdmissionFenceOutcome.Acquired"/>.</summary>
public sealed record AdmissionFenceAcquireResult(AdmissionFenceOutcome Outcome, IAdmissionFenceSession? Session, string? Detail = null);
