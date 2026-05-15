namespace kapacitor;

static class PathHelpers {
    public static string HomeDirectory {
        get {
            var home = Environment.GetEnvironmentVariable("HOME");
            if (string.IsNullOrWhiteSpace(home) || !Path.IsPathRooted(home))
                home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return home;
        }
    }

    static readonly string ConfigDir = Environment.GetEnvironmentVariable("KAPACITOR_CONFIG_DIR") ?? Path.Combine(HomeDirectory, ".config", "kapacitor");

    public static string ConfigPath(string name) => Path.Combine(ConfigDir, name);
}
