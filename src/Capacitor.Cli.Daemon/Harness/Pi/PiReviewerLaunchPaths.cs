namespace Capacitor.Cli.Daemon.Harness.Pi;

internal sealed record PiReviewerLaunchPaths(
    string Dir, string Extension, string Manifest, string SystemPrompt, string Sessions, string Ready);
