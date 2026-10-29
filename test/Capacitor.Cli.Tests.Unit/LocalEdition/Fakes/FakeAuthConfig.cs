namespace Capacitor.Cli.Tests.Unit.LocalEdition.Fakes;

/// <summary>What <see cref="FakeLocalServer"/> answers on <c>/auth/config</c>.</summary>
public enum FakeAuthConfig {
    /// <summary>200, provider None, with the pending-installation header.</summary>
    Pending,

    /// <summary>200, provider None, no header.</summary>
    Lifted,

    /// <summary>403.</summary>
    Forbidden,
}
