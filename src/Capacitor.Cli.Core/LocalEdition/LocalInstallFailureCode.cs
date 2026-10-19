namespace Capacitor.Cli.Core.LocalEdition;

public enum LocalInstallFailureCode {
    UnsupportedPlatform,
    MuslUnsupported,
    SocketPathTooLong,
    UnsafeRoot,
    RegistryUnreadable,
    ConfigUnreadable,
    OtherServer,
    LeftoverState,
    AnswerRequired,
    InvalidArguments,
    LifecycleBusy,
    BundleNotPublished,
    DownloadFailed,
    VerificationFailed,
    StartFailed,
    NotOurServer,
    CommitFailed,
    RecoveryFailed,
    Cancelled,
    ClientBelowFloor
}

public static class LocalInstallFailureCodeExtensions {
    /// <summary>The snake_case name that reaches events and logs.</summary>
    public static string Wire(this LocalInstallFailureCode code) => code switch {
        LocalInstallFailureCode.UnsupportedPlatform => "unsupported_platform",
        LocalInstallFailureCode.MuslUnsupported     => "musl_unsupported",
        LocalInstallFailureCode.SocketPathTooLong   => "socket_path_too_long",
        LocalInstallFailureCode.UnsafeRoot          => "unsafe_root",
        LocalInstallFailureCode.RegistryUnreadable  => "registry_unreadable",
        LocalInstallFailureCode.ConfigUnreadable    => "config_unreadable",
        LocalInstallFailureCode.OtherServer         => "other_server",
        LocalInstallFailureCode.LeftoverState       => "leftover_state",
        LocalInstallFailureCode.AnswerRequired      => "answer_required",
        LocalInstallFailureCode.InvalidArguments    => "invalid_arguments",
        LocalInstallFailureCode.LifecycleBusy       => "lifecycle_busy",
        LocalInstallFailureCode.BundleNotPublished  => "bundle_not_published",
        LocalInstallFailureCode.DownloadFailed      => "download_failed",
        LocalInstallFailureCode.VerificationFailed  => "verification_failed",
        LocalInstallFailureCode.StartFailed         => "start_failed",
        LocalInstallFailureCode.NotOurServer        => "not_our_server",
        LocalInstallFailureCode.CommitFailed        => "commit_failed",
        LocalInstallFailureCode.RecoveryFailed      => "recovery_failed",
        LocalInstallFailureCode.Cancelled           => "cancelled",
        LocalInstallFailureCode.ClientBelowFloor    => "client_below_floor",
        _                                           => throw new ArgumentOutOfRangeException(nameof(code), code, null)
    };
}
