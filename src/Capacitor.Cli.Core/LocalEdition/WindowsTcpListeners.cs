using System.Buffers.Binary;
using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Capacitor.Cli.Core.LocalEdition;

/// <summary>The listening half of Windows' TCP tables, which name each listener's owning process without spawning
/// <c>netstat</c>, whose state column is localized.</summary>
[SupportedOSPlatform("windows")]
static partial class WindowsTcpListeners {
    const int AfInet  = 2;
    const int AfInet6 = 23;

    const int TcpTableOwnerPidListener = 3;
    const uint ErrorInsufficientBuffer = 122;

    const int Row4Size = 24;
    const int Row6Size = 56;

    /// <summary>The pid listening on <paramref name="port"/> where a loopback client would reach it: on loopback or
    /// on every address. Null when nothing does or the table cannot be read.</summary>
    public static int? Owner(int port) => Owner(AfInet, port) ?? Owner(AfInet6, port);

    static int? Owner(int family, int port) {
        var size = 0;
        var result = GetExtendedTcpTable(0, ref size, false, family, TcpTableOwnerPidListener, 0);

        for (var attempt = 0; attempt < 3 && result == ErrorInsufficientBuffer; attempt++) {
            var buffer = Marshal.AllocHGlobal(size);
            try {
                result = GetExtendedTcpTable(buffer, ref size, false, family, TcpTableOwnerPidListener, 0);
                if (result == 0) return Find(buffer, family, port);
            } finally {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return null;
    }

    static int? Find(nint table, int family, int port) {
        var count = Marshal.ReadInt32(table);

        for (var i = 0; i < count; i++) {
            var row = table + 4 + i * (family == AfInet ? Row4Size : Row6Size);

            if (family == AfInet) {
                // MIB_TCPROW_OWNER_PID: state, local address, local port, remote address, remote port, pid.
                var address = new IPAddress((uint)Marshal.ReadInt32(row, 4));
                if (LocalPort(row, 8) == port && (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any)))
                    return Marshal.ReadInt32(row, 20);
            } else {
                // MIB_TCP6ROW_OWNER_PID: local address[16], scope, local port, remote address[16], scope, remote port, state, pid.
                var bytes = new byte[16];
                Marshal.Copy(row, bytes, 0, 16);
                var address = new IPAddress(bytes);
                if (LocalPort(row, 20) == port && (IPAddress.IsLoopback(address) || address.Equals(IPAddress.IPv6Any)))
                    return Marshal.ReadInt32(row, 52);
            }
        }

        return null;
    }

    /// <summary>The low two bytes, in network order.</summary>
    static int LocalPort(nint row, int offset) => BinaryPrimitives.ReverseEndianness((ushort)Marshal.ReadInt32(row, offset));

    [LibraryImport("iphlpapi.dll")]
    private static partial uint GetExtendedTcpTable(
        nint table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order, int family, int tableClass, int reserved);
}
