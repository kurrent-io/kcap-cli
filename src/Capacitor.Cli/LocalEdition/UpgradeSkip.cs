namespace Capacitor.Cli.LocalEdition;

/// <summary>Why <c>kcap local upgrade</c> changed nothing.</summary>
enum UpgradeSkip {
    /// <summary>The stack already runs the caller's version.</summary>
    Current,

    /// <summary>The stack is newer than the caller, which keeps working against it.</summary>
    StackNewer,

    NotInstalled,
}

static class UpgradeSkipExtensions {
    public static string Wire(this UpgradeSkip skip) => skip switch {
        UpgradeSkip.Current      => "current",
        UpgradeSkip.StackNewer   => "stack_newer",
        UpgradeSkip.NotInstalled => "not_installed",
        _                        => throw new ArgumentOutOfRangeException(nameof(skip), skip, null),
    };
}
