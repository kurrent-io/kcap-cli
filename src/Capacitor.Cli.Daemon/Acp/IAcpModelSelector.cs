using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Acp;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Acp;

/// <summary>
/// Per-vendor hook for resolving + applying a requested model against an ACP session, called once
/// from AcpHostedAgentRuntime.StartAsync after session/new resolves and before the first
/// session/prompt fires.
///
/// <b>Cancellation contract (spec-review Finding 2):</b> a canceled <paramref name="ct"/> is NOT
/// one of the "never throws" failure modes below — <see cref="AcpConnection.RequestAsync"/> throws
/// <see cref="OperationCanceledException"/> when <paramref name="ct"/> is canceled, and every
/// implementation of this method MUST let that propagate uncaught, aborting <c>StartAsync</c>
/// entirely (no runtime is ever handed back to a caller who already canceled the launch). Only a
/// best-effort RESOLUTION failure — no requested model, an unparsable/missing <c>session/new</c>
/// <c>models</c> object, no match in <c>availableModels</c>, or a well-formed JSON-RPC ERROR
/// response to whatever RPC this selector sends — returns <see langword="null"/> and lets
/// <c>StartAsync</c> continue to the first prompt with the vendor's own default model.
/// <see cref="ConfigOptionModelSelector.TrySelectAsync"/>'s <c>catch (Exception ex) when (ex is not
/// OperationCanceledException)</c> around its `session/set_config_option` call is what enforces
/// this split for Cursor: an <see cref="OperationCanceledException"/> is deliberately NOT caught by
/// that guard and propagates straight out of `TrySelectAsync`, then out of `StartAsync`. The
/// earlier `catch (JsonException ex)` around the `models` parse is narrower still — a
/// <see cref="System.Text.Json.JsonException"/> is never an
/// <see cref="OperationCanceledException"/>, so it structurally cannot swallow one either; a future
/// implementation of this interface must preserve the same shape (catch concrete failure types,
/// never a bare `catch (Exception)` that would also eat cancellation).
/// </summary>
internal interface IAcpModelSelector {
    Task<string?> TrySelectAsync(
        AcpConnection     connection,
        string            sessionId,
        JsonElement       sessionNewResult,
        string?           requestedModel,
        ILogger           logger,
        CancellationToken ct);
}

/// <summary>
/// Today's original Cursor behavior, unchanged, generalized to take sessionId/connection as
/// parameters instead of reading AcpHostedAgentRuntime's private fields: parses session/new's
/// `models.availableModels` via AcpModelResolver.Resolve, and — on a match — sends
/// session/set_config_option {sessionId, configId: "model", value} and awaits it. Every failure
/// mode (missing/unparsable `models`, no match, a JSON-RPC error response) is caught and logged,
/// never fatal — matches the "model selection is a nice-to-have, never a launch precondition"
/// contract from docs/ai-688-cursor-prototype-findings.md.
/// </summary>
internal sealed class ConfigOptionModelSelector : IAcpModelSelector {
    public static readonly ConfigOptionModelSelector Instance = new();

    public async Task<string?> TrySelectAsync(
            AcpConnection connection, string sessionId, JsonElement sessionNewResult,
            string? requestedModel, ILogger logger, CancellationToken ct) {
        if (string.IsNullOrWhiteSpace(requestedModel))
            return null;

        AvailableModelDto[]? availableModels = null;

        if (sessionNewResult.TryGetProperty("models", out var modelsElement)) {
            try {
                availableModels = JsonSerializer
                    .Deserialize(modelsElement.GetRawText(), CapacitorJsonContext.Default.SessionModelsInfo)
                    ?.AvailableModels;
            } catch (JsonException ex) {
                logger.LogDebug(ex, "ACP: failed to parse session/new 'models' object; skipping model selection.");
                return null;
            }
        }

        var resolvedModelId = AcpModelResolver.Resolve(requestedModel, availableModels);
        if (resolvedModelId is null) {
            logger.LogWarning(
                "ACP: requested model '{RequestedModel}' was not found in session/new's availableModels; continuing with the vendor's default model.",
                requestedModel);
            return null;
        }

        var setConfigOptionParams = JsonSerializer.SerializeToElement(
            new SetConfigOptionParams(SessionId: sessionId, ConfigId: "model", Value: resolvedModelId),
            CapacitorJsonContext.Default.SetConfigOptionParams);

        try {
            await connection.RequestAsync("session/set_config_option", setConfigOptionParams, ct).ConfigureAwait(false);
            return resolvedModelId;
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            logger.LogWarning(ex,
                "ACP: session/set_config_option failed for model '{ResolvedModelId}'; continuing with the vendor's default model.",
                resolvedModelId);
            return null;
        }
    }
}

/// <summary>Used by a descriptor whose vendor has no model-selection hook at all — never touches
/// the wire, never inspects sessionNewResult. <b>Round 2 Finding 2:</b> there is no
/// SupportsModelSelection flag to check this instance against — a vendor opts out of model
/// selection simply by carrying THIS instance as its ModelSelector, and
/// AcpHostedAgentRuntimeFactory.StartAsync (D4) forwards descriptor.ModelSelector unconditionally
/// for every descriptor. This type is exactly as valid a ModelSelector as
/// ConfigOptionModelSelector.Instance — the object itself is the whole contract, not a paired
/// boolean.</summary>
internal sealed class NoOpModelSelector : IAcpModelSelector {
    public static readonly NoOpModelSelector Instance = new();

    public Task<string?> TrySelectAsync(
            AcpConnection connection, string sessionId, JsonElement sessionNewResult,
            string? requestedModel, ILogger logger, CancellationToken ct) {
        if (!string.IsNullOrWhiteSpace(requestedModel))
            logger.LogDebug("ACP: this vendor has no model-selection hook; continuing with its default model.");
        return Task.FromResult<string?>(null);
    }
}
