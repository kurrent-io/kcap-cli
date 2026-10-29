using System.Text.Json;

namespace Capacitor.Cli.Core.Eval;

/// <summary>Writes a completed <see cref="BaselineOutput"/> as JSON via the source-generated
/// <see cref="CapacitorJsonContext"/> — the AOT-compiled CLI has no reflection-based fallback.</summary>
public static class BaselineOutputWriter {
    public static void Write(string path, BaselineOutput output) {
        var json = JsonSerializer.Serialize(output, CapacitorJsonContext.Default.BaselineOutput);
        File.WriteAllText(path, json);
    }
}
