using Capacitor.Remote.Models;

namespace Capacitor.App.Services;

/// An ACP question: options are identified by OptionId (labels may repeat), and a multi-select
/// answer must pick between MinSelections and MaxSelections of them. No options means free text.
public sealed record AcpElicitation(string Prompt, IReadOnlyList<AcpInteractionOption> Options, bool IsMultiSelect, int? MinSelections, int? MaxSelections);
