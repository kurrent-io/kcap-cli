using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

[JsonSerializable(typeof(AgentInstanceDto))]
[JsonSerializable(typeof(AgentInstanceDto[]))]
[JsonSerializable(typeof(DaemonInfo))]
[JsonSerializable(typeof(List<DaemonInfo>))]
[JsonSerializable(typeof(AcpInteractionOption))]
[JsonSerializable(typeof(AcpInteractionOption[]))]
[JsonSerializable(typeof(PermissionResponsePayload))]
[JsonSerializable(typeof(SessionDetailDto))]
[JsonSerializable(typeof(SessionEventDto))]
[JsonSerializable(typeof(Dictionary<string, VendorModelOptionDto[]>))]
public partial class RemoteModelsJsonContext : JsonSerializerContext;
