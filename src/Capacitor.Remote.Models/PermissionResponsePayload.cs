using System.Text.Json;
using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

/// Body of POST api/sessions/{sessionId}/permission-response/{requestId}. Unset members are
/// omitted: the server canonicalizes the selection lists and rejects a count mismatch with 400.
public sealed record PermissionResponsePayload {
    [JsonPropertyName("behavior")] public required string Behavior { get; init; }
    [JsonPropertyName("apply_permissions"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? ApplyPermissions { get; init; }
    [JsonPropertyName("updated_input"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? UpdatedInput { get; init; }
    [JsonPropertyName("selected_option_id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SelectedOptionId { get; init; }
    [JsonPropertyName("selected_option_label"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SelectedOptionLabel { get; init; }
    [JsonPropertyName("free_text"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FreeText { get; init; }
    [JsonPropertyName("selected_option_ids"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? SelectedOptionIds { get; init; }
    [JsonPropertyName("selected_option_labels"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? SelectedOptionLabels { get; init; }
}
