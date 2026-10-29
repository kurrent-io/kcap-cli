using System.Xml;
using System.Xml.Linq;

namespace Capacitor.App.Platform.Mac;

/// <summary>
/// An app bundle verified by its Info.plist as kcap. <see cref="Legacy"/> is the pre-rebrand identity
/// (io.kurrent.capacitor, "Kurrent Capacitor.app"): its installs follow the same update feed and are
/// updated in place, so they keep appearing until something replaces them.
/// </summary>
public sealed record KcapBundle(string Root, string Version, string Executable, bool Legacy) {
    public const string BundleName = "kcap.app";
    public const string LegacyBundleName = "Kurrent Capacitor.app";
    public const string ExecutableName = "kcap-desktop";
    public const string LegacyExecutableName = "Kurrent Capacitor";
    const string Identifier = "io.kurrent.kcap";
    const string LegacyIdentifier = "io.kurrent.capacitor";

    public static IReadOnlyList<string> ExecutableNames { get; } = [ExecutableName, LegacyExecutableName];
    public static IReadOnlyList<string> Identifiers { get; } = [Identifier, LegacyIdentifier];

    public static KcapBundle? Read(string root) {
        try {
            if (!Directory.Exists(root) || new DirectoryInfo(root).LinkTarget is not null) return null;
            using var reader = XmlReader.Create(Path.Combine(root, "Contents", "Info.plist"),
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
            var entries = XDocument.Load(reader).Root?.Element("dict")?.Elements().ToArray();
            if (entries is null) return null;
            string? Value(string key) {
                for (var i = 0; i + 1 < entries.Length; i += 2)
                    if (entries[i].Name == "key" && entries[i].Value == key && entries[i + 1].Name == "string")
                        return entries[i + 1].Value;
                return null;
            }
            if ((Value("CFBundleVersion") ?? Value("CFBundleShortVersionString")) is not { Length: > 0 } version) return null;
            var identifier = Value("CFBundleIdentifier");
            var executable = identifier switch {
                Identifier => ExecutableName,
                LegacyIdentifier => Value("CFBundleExecutable") ?? LegacyExecutableName,
                _ => null,
            };
            if (executable is null || !ExecutableNames.Contains(executable)
                    || !File.Exists(Path.Combine(root, "Contents", "MacOS", executable))) return null;
            return new KcapBundle(root, version, executable, identifier == LegacyIdentifier);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException) {
            return null;
        }
    }
}
