using Capacitor.Cli.Core.FirstRun;
using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli.Commands;

/// <summary>One privacy level of a detached import plan: the arguments of one uncapped pass.</summary>
internal sealed record ImportPlanLevel(
    FirstRunImportLevel                 Level,
    IReadOnlyList<FirstRunImportChoice> Repos,
    DateOnly?                           Since,
    IReadOnlyList<HarnessId>?           Vendors,
    bool                                SkipTitle);
