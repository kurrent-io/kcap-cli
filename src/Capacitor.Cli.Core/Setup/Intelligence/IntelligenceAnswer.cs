namespace Capacitor.Cli.Core.Setup.Intelligence;

/// <summary>Who runs background intelligence, named by one token: a local agent's harness, a provider's vendor, or
/// <c>none</c>.</summary>
public sealed record IntelligenceAnswer(string Mode, string? Harness, IntelligenceVendor? Vendor) {
    public const string NoneToken = "none";

    public static IReadOnlyList<string> Harnesses { get; } = [HeadlessHarnessProbe.Claude, HeadlessHarnessProbe.Codex];

    /// <summary>Every token <see cref="Parse"/> accepts.</summary>
    public static IReadOnlyList<string> Tokens { get; } = [.. Harnesses, .. IntelligenceVendor.All.Select(v => v.Id), NoneToken];

    public static IntelligenceAnswer None { get; } = new(IntelligenceModes.None, null, null);

    public static IntelligenceAnswer LocalAgent(string harness) => new(IntelligenceModes.LocalAgent, harness, null);

    public static IntelligenceAnswer Provider(IntelligenceVendor vendor) => new(IntelligenceModes.Provider, null, vendor);

    public static IntelligenceAnswer? Parse(string? token) {
        var name = token?.Trim().ToLowerInvariant();

        return name switch {
            null or ""                          => null,
            NoneToken                           => None,
            _ when Harnesses.Contains(name)     => LocalAgent(name),
            _ => IntelligenceVendor.All.FirstOrDefault(v => v.Id == name) is { } vendor ? Provider(vendor) : null,
        };
    }

    public string Token => Harness ?? Vendor?.Id ?? NoneToken;

    /// <param name="model">The provider's model, or null for the server's default; the vendor's own default stands in
    /// where the server has none.</param>
    public IntelligenceSetupRequest Request(string? model, string? apiKey, bool? importOutcomes) =>
        Vendor is { } vendor
            ? new(Mode, null, vendor.Id, string.IsNullOrWhiteSpace(model) ? vendor.ServerHasDefault ? null : vendor.DefaultModel : model.Trim(),
                apiKey, importOutcomes)
            : new(Mode, Harness, ImportOutcomes: importOutcomes);
}
