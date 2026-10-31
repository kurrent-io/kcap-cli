using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Capacitor.Cli.Core.Harness.Codex;

/// <summary>Identifies the Codex files a hook-trust answer was given for: <c>hooks.json</c> then
/// <c>config.toml</c>, each as a presence flag, an 8-byte little-endian length and its bytes, so an absent file,
/// an empty one and bytes moved between the two all differ. A file that exists but cannot be read yields null: no answer can match it.</summary>
public static class CodexTrustFingerprint {
    public static string? Of(CodexPaths paths) {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        if (!Append(hash, paths.UserHooksJson) || !Append(hash, paths.ConfigToml)) return null;
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    static bool Append(IncrementalHash hash, string path) {
        byte[] bytes;
        try {
            // The agent writes these files, so the read must not deny it write access.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            bytes = buffer.ToArray();
        } catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) {
            hash.AppendData([0]);
            return true;
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            return false;
        }

        Span<byte> length = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(length, bytes.Length);
        hash.AppendData([1]);
        hash.AppendData(length);
        hash.AppendData(bytes);
        return true;
    }
}
