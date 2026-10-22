namespace Capacitor.Remote.Models;

public static class TerminalScrollKey {
    const string Prefix = "Wheel:";
    const int MaxBytes = 24;

    public static bool TryCreate(ReadOnlySpan<byte> bytes, out string key) {
        key = "";
        if (!IsWheel(bytes)) return false;
        key = Prefix + Convert.ToBase64String(bytes);
        return true;
    }

    public static bool TryDecode(string? key, out byte[] bytes) {
        bytes = [];
        if (key is null || !key.StartsWith(Prefix, StringComparison.Ordinal) || key.Length > Prefix.Length + 32) return false;
        Span<byte> buffer = stackalloc byte[MaxBytes];
        if (!Convert.TryFromBase64String(key[Prefix.Length..], buffer, out var count) || !IsWheel(buffer[..count])) return false;
        bytes = buffer[..count].ToArray();
        return true;
    }

    static bool IsWheel(ReadOnlySpan<byte> bytes) {
        if (bytes is [0x1b, (byte)'[', (byte)'M', 96 or 97, >= 33, >= 33]) return true;
        if (bytes.Length is < 10 or > MaxBytes || !bytes.StartsWith("\x1b[<"u8) || bytes[^1] != 'M') return false;
        if (bytes[3] != '6' || bytes[4] is not ((byte)'4' or (byte)'5') || bytes[5] != ';') return false;
        var coords = bytes[6..^1];
        var separator = coords.IndexOf((byte)';');
        return separator > 0 && Coordinate(coords[..separator], 500) && Coordinate(coords[(separator + 1)..], 200);
    }

    static bool Coordinate(ReadOnlySpan<byte> text, int maximum) {
        if (text.Length is < 1 or > 3) return false;
        var value = 0;
        foreach (var digit in text) {
            if (digit is < (byte)'0' or > (byte)'9') return false;
            value = value * 10 + digit - '0';
        }
        return value > 0 && value <= maximum;
    }
}
