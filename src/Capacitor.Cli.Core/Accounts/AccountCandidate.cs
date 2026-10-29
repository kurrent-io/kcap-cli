using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli.Core.Accounts;

public sealed record AccountCandidate(HarnessId Vendor, string Directory, string Reason);
