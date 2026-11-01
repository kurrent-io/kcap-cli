using Capacitor.Cli.Core.Setup.Intelligence;

namespace Capacitor.Cli.Commands;

/// <summary>What setup asks a person about background intelligence.</summary>
interface IIntelligenceQuestions {
    /// <returns>A harness from <paramref name="harnesses"/>, <see cref="LocalIntelligenceStep.ProviderChoice"/>, or
    /// <see cref="IntelligenceAnswer.NoneToken"/> for not now.</returns>
    string Choose(IReadOnlyList<string> harnesses);

    IntelligenceVendor Vendor();

    string Model(IntelligenceVendor vendor);

    /// <summary>Asks for the key without echoing it.</summary>
    string? Key(IntelligenceVendor vendor);

    bool ImportOutcomes();
}
