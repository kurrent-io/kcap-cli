using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Accounts;

namespace Capacitor.Cli.Core.Harness.Claude;

/// <summary>kcap's status line in a Claude <c>settings.json</c>. Only <c>statusLine.command</c> and its
/// <c>type</c> are kcap's; every other <c>statusLine</c> property stays the user's and is never copied or restored.</summary>
public static class ClaudeStatusLine {
    const string Key     = "statusLine";
    const string Command = "command";
    const string Type    = "type";

    /// <summary>The displaced command is handed to <paramref name="saveBackup"/> before the settings are
    /// written, and nothing is written unless it returns true: an interruption leaves the original with a
    /// backup, which a re-wire completes. Our own command, for any account, is never backed up.</summary>
    public static SettingsEdit Install(
        string                       settingsPath,
        string                       accountId,
        StatusLineBackup?            backup,
        Func<StatusLineBackup, bool> saveBackup,
        bool                         posixShell
    ) =>
        JsonSettingsFile.Edit(settingsPath, root => {
            var current = CommandOf(root);

            if (ClaudeStatusLineCommand.AccountOf(current) is { } installed) {
                var original = backup?.Original;

                // A registry entry lost and re-adopted under a new id still has its original inside our guard.
                if (backup is null && ClaudeStatusLineCommand.TryOriginalOf(current!, out var embedded)) {
                    if (!saveBackup(new StatusLineBackup(embedded))) return false;
                    original = embedded;
                }

                if (installed == accountId) return false;

                Write(root, ClaudeStatusLineCommand.For(accountId, original, posixShell));
                return true;
            }

            if (!saveBackup(new StatusLineBackup(current))) return false;

            Write(root, ClaudeStatusLineCommand.For(accountId, current, posixShell));
            return true;
        });

    /// <summary>Acts only while our command is installed. Without a backup it removes our command rather than
    /// guess at an original, and says so.</summary>
    public static (SettingsEdit Edit, bool BackupMissing) Remove(string settingsPath, StatusLineBackup? backup) {
        var missing = false;

        var edit = JsonSettingsFile.Edit(settingsPath, root => {
            if (!ClaudeStatusLineCommand.IsOurs(CommandOf(root))) return false;

            if (backup?.Original is { } original) {
                Write(root, original);
                return true;
            }

            missing = backup is null;
            var line = root[Key]!.AsObject();
            line.Remove(Command);
            line.Remove(Type);
            if (line.Count == 0) root.Remove(Key);
            return true;
        }, createIfMissing: false);

        return (edit, missing);
    }

    static string? CommandOf(JsonObject root) =>
        root[Key] switch {
            null            => null,
            JsonObject line => line[Command] switch {
                null                                                 => null,
                JsonValue value when value.TryGetValue(out string? s) => s,
                _                                                    => throw JsonSettingsFile.Refuse(),
            },
            _ => throw JsonSettingsFile.Refuse(),
        };

    static void Write(JsonObject root, string command) {
        if (root[Key] is not JsonObject line) {
            line      = [];
            root[Key] = line;
        }

        line[Type]    = "command";
        line[Command] = command;
    }
}
