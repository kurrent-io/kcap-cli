namespace Capacitor.Cli.Services;

sealed record ServicePipeResponse(bool Ok, string? Error = null, ServicePipeStatus? Status = null, ServicePipeCapabilities? Capabilities = null);
