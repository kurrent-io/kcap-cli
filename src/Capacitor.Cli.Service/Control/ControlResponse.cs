namespace Capacitor.Cli.Service.Control;

public sealed record ControlResponse(bool Ok, string? Error = null, StatusDto? Status = null, CapabilitiesDto? Capabilities = null);
