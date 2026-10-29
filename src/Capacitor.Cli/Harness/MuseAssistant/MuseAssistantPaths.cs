using System.Security.Cryptography;
using System.Text;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Harness.MuseAssistant;

/// <summary>Where Muse Assistant recording state lives. Chat ids and user ids are opaque strings from
/// another system, so neither is ever a path segment: the directory and file names are hashes and the
/// raw values sit inside the files they name.</summary>
public static class MuseAssistantPaths {
    public const string Root = "muse-assistant";

    public static string ScopeDir(ConfigRoot config, MuseAssistantPrincipal principal) =>
        config.Path(Root, Sha256Hex(principal.ServerUrl + "\n" + principal.UserId));

    public static string ChatKey(string chatId) => Sha256Hex(chatId);

    public static string ScopeFile(string scopeDir) => Path.Join(scopeDir, "scope.json");
    public static string ScopeLock(string scopeDir) => Path.Join(scopeDir, "scope.lock");
    public static string ChatState(string scopeDir, string chatId) => Path.Join(scopeDir, ChatKey(chatId) + ".json");
    public static string ChatLock(string scopeDir, string chatId) => Path.Join(scopeDir, ChatKey(chatId) + ".lock");

    static string Sha256Hex(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
