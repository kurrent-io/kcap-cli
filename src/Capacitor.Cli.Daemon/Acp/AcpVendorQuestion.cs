namespace Capacitor.Cli.Daemon.Acp;

/// <summary>One question a vendor asks through its own client method. With no options the answer is typed.</summary>
internal sealed record AcpVendorQuestion(string Text, IReadOnlyList<AcpVendorQuestionOption> Options, bool MultiSelect);
