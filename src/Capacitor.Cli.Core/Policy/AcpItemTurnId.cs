using System.Security.Cryptography;
using System.Text;

namespace Capacitor.Cli.Core.Policy;

/// The id a hosted turn is declared by: the daemon cannot know the event id the server will store, but it knows
/// the vendor's item id. RFC 4122 version 5 under a fixed namespace, written the same way on the server; both pin
/// the same vectors, and changing either side breaks every hosted turn set.
public static class AcpItemTurnId {
    static readonly Guid Namespace = new("8a3c5e1f-4b7d-5c2a-9e6f-1d2b3c4d5e6f");

    public static Guid For(string itemId) {
        Span<byte> ns = stackalloc byte[16];
        Namespace.TryWriteBytes(ns, bigEndian: true, out _);

        var name  = Encoding.UTF8.GetBytes(itemId);
        var input = new byte[16 + name.Length];
        ns.CopyTo(input);
        name.CopyTo(input, 16);

        Span<byte> hash = stackalloc byte[20];
#pragma warning disable CA5350 // RFC 4122 version 5 is defined over SHA-1; the id names a turn, it protects nothing.
        SHA1.HashData(input, hash);
#pragma warning restore CA5350

        hash[6] = (byte)((hash[6] & 0x0F) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);

        return new Guid(hash[..16], bigEndian: true);
    }
}
