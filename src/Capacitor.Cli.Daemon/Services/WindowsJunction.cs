using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// A directory junction made through the reparse-point API. A junction needs no privilege, unlike a
/// symlink, and the paths stay opaque: going through <c>cmd /c mklink /J</c> lets cmd reparse a path, so
/// an <c>&amp;</c> in it runs a command and a <c>%VAR%</c> in it expands.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class WindowsJunction {
    const uint IoReparseTagMountPoint  = 0xA0000003;
    const uint FsctlSetReparsePoint    = 0x000900A4;
    const uint GenericWrite            = 0x40000000;
    const uint OpenExisting            = 3;
    const uint FileFlagOpenReparsePoint = 0x00200000;
    const uint FileFlagBackupSemantics = 0x02000000;

    /// <summary>Creates <paramref name="link"/> as an empty directory and points it at
    /// <paramref name="target"/>. The link must not exist yet; a failed attempt removes it again.</summary>
    public static void Create(string link, string target) {
        var fullTarget = Path.GetFullPath(target);
        Directory.CreateDirectory(link);

        try {
            using var handle = CreateFileW(link, GenericWrite, 0, 0, OpenExisting,
                FileFlagOpenReparsePoint | FileFlagBackupSemantics, 0);
            if (handle.IsInvalid)
                throw new IOException($"could not open '{link}' to make it a junction: {Marshal.GetLastPInvokeErrorMessage()}");

            var buffer = MountPointBuffer(fullTarget);
            if (!DeviceIoControl(handle, FsctlSetReparsePoint, buffer, (uint)buffer.Length, 0, 0, out _, 0))
                throw new IOException($"could not point '{link}' at '{fullTarget}': {Marshal.GetLastPInvokeErrorMessage()}");
        } catch {
            try { Directory.Delete(link); } catch (IOException) { }
            throw;
        }
    }

    /// <summary>A <c>REPARSE_DATA_BUFFER</c> for a mount point: the NT substitute name, then the
    /// printable name, each NUL-terminated UTF-16.</summary>
    internal static byte[] MountPointBuffer(string fullTarget) {
        var substitute = @"\??\" + fullTarget;
        var subBytes   = substitute.Length * 2;
        var printBytes = fullTarget.Length * 2;
        var pathBytes  = subBytes + 2 + printBytes + 2;
        var dataLength = 8 + pathBytes;

        var buffer = new byte[8 + dataLength];
        var span   = buffer.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, IoReparseTagMountPoint);
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..], (ushort)dataLength);
        BinaryPrimitives.WriteUInt16LittleEndian(span[8..], 0);
        BinaryPrimitives.WriteUInt16LittleEndian(span[10..], (ushort)subBytes);
        BinaryPrimitives.WriteUInt16LittleEndian(span[12..], (ushort)(subBytes + 2));
        BinaryPrimitives.WriteUInt16LittleEndian(span[14..], (ushort)printBytes);
        MemoryMarshal.AsBytes(substitute.AsSpan()).CopyTo(span[16..]);
        MemoryMarshal.AsBytes(fullTarget.AsSpan()).CopyTo(span[(16 + subBytes + 2)..]);
        return buffer;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFileW(
        string fileName, uint desiredAccess, uint shareMode, nint securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(
        SafeFileHandle device, uint ioControlCode, byte[] inBuffer, uint inBufferSize,
        nint outBuffer, uint outBufferSize, out uint bytesReturned, nint overlapped);
}
