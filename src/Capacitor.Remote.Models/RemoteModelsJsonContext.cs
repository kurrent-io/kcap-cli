using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

[JsonSerializable(typeof(AgentInstanceDto))]
[JsonSerializable(typeof(AgentInstanceDto[]))]
[JsonSerializable(typeof(DaemonInfo))]
[JsonSerializable(typeof(List<DaemonInfo>))]
[JsonSerializable(typeof(DaemonAccountsDto))]
[JsonSerializable(typeof(List<DaemonAccountsDto>))]
[JsonSerializable(typeof(ListedAccountDto))]
[JsonSerializable(typeof(AcpInteractionOption))]
[JsonSerializable(typeof(AcpInteractionOption[]))]
[JsonSerializable(typeof(PermissionResponsePayload))]
[JsonSerializable(typeof(SessionDetailDto))]
[JsonSerializable(typeof(SessionEventDto))]
[JsonSerializable(typeof(SubagentEventsDto))]
[JsonSerializable(typeof(QueuedInputItem[]))]
[JsonSerializable(typeof(DiscardQueuedInputRequest))]
[JsonSerializable(typeof(Dictionary<string, VendorModelOptionDto[]>))]
[JsonSerializable(typeof(RepoSessionsResponseDto))]
[JsonSerializable(typeof(RepoSessionPullRequestDto[]))]
[JsonSerializable(typeof(SessionOwnerOptionDto[]))]
public partial class RemoteModelsJsonContext : JsonSerializerContext;
