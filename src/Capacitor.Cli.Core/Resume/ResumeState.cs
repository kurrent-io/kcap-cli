using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Resume;

public enum ResumeState {
    [JsonStringEnumMemberName("running")]     Running,
    [JsonStringEnumMemberName("resuming")]    Resuming,
    [JsonStringEnumMemberName("interrupted")] Interrupted,
}
