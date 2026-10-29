using System.Runtime.Versioning;
using Capacitor.Cli.Core.Setup;
using Microsoft.Win32;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Core.Tests.Unit.Setup;

/// Runs against a throwaway key under HKCU\Software, never the real HKCU\Environment.
[SupportedOSPlatform("windows")]
public class WindowsUserPathStoreTests {
    static string ScratchKey() => $@"Software\KcapTests\{Guid.NewGuid():N}";

    static void Delete(string key) => Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false);

    /// The reason the store exists: an expandable entry must survive the append unexpanded, and the
    /// value must stay REG_EXPAND_SZ.
    [Test, RunOn(OS.Windows)]
    public async Task Append_keeps_expandable_entries_unexpanded() {
        var key = ScratchKey();
        try {
            using (var k = Registry.CurrentUser.CreateSubKey(key)) {
                k.SetValue("Path", @"%USERPROFILE%\bin;C:\tools", RegistryValueKind.ExpandString);
            }

            var store = new WindowsUserPathStore(key);
            store.Append(@"C:\Apps\Kcap");

            await Assert.That(store.Read()).IsEqualTo(@"%USERPROFILE%\bin;C:\tools;C:\Apps\Kcap");
            using var read = Registry.CurrentUser.OpenSubKey(key)!;
            await Assert.That(read.GetValueKind("Path")).IsEqualTo(RegistryValueKind.ExpandString);
        } finally {
            Delete(key);
        }
    }

    [Test, RunOn(OS.Windows)]
    public async Task Append_to_an_absent_value_creates_it() {
        var key = ScratchKey();
        try {
            var store = new WindowsUserPathStore(key);
            await Assert.That(store.Read()).IsNull();

            store.Append(@"C:\Apps\Kcap");

            await Assert.That(store.Read()).IsEqualTo(@"C:\Apps\Kcap");
        } finally {
            Delete(key);
        }
    }

    [Test, RunOn(OS.Windows)]
    public async Task Append_does_not_double_a_trailing_separator() {
        var key = ScratchKey();
        try {
            using (var k = Registry.CurrentUser.CreateSubKey(key)) {
                k.SetValue("Path", @"C:\tools;", RegistryValueKind.String);
            }

            var store = new WindowsUserPathStore(key);
            store.Append(@"C:\Apps\Kcap");

            await Assert.That(store.Read()).IsEqualTo(@"C:\tools;C:\Apps\Kcap");
            using var read = Registry.CurrentUser.OpenSubKey(key)!;
            await Assert.That(read.GetValueKind("Path")).IsEqualTo(RegistryValueKind.String);
        } finally {
            Delete(key);
        }
    }
}
