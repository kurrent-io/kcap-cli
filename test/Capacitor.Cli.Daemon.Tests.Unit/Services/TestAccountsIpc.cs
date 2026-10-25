using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>An accounts handler over a publisher with no store, for tests that start a control server.</summary>
static class TestAccountsIpc {
    public static AccountsIpc Inert() =>
        new(new AccountsPublisher(new DaemonConfig(), new UserHome(typeof(TestAccountsIpc).Assembly.Location), TimeProvider.System,
            NullLogger<AccountsPublisher>.Instance));
}
