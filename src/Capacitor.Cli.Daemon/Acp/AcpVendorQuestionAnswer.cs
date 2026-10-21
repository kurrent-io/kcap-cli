namespace Capacitor.Cli.Daemon.Acp;

/// <summary>The labels chosen for one question, or the one typed answer.</summary>
internal sealed record AcpVendorQuestionAnswer(AcpVendorQuestion Question, IReadOnlyList<string> Answers);
