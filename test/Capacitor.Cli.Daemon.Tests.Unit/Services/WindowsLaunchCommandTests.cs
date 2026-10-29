using System.Diagnostics;
using Capacitor.Cli.Core.Setup;
using Capacitor.Cli.Daemon.Pty.Windows;
using Capacitor.Cli.Daemon.Services;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// A vendor the daemon advertises because its npm `.cmd` shim is on PATH must also be startable
/// without a shell: CreateProcess appends only `.exe` to a bare name.
public class WindowsLaunchCommandTests {
    const string Prefix = @"C:\npm";

    static string? OnPath(string command) => command switch {
        "gemini"   => $@"{Prefix}\gemini.cmd",
        "opencode" => $@"{Prefix}\opencode.cmd",
        "legacy"   => $@"{Prefix}\legacy.cmd",
        "pi"       => @"C:\bun\pi.exe",
        "node"     => @"C:\node\node.exe",
        _          => null,
    };

    static NpmCmdShim.Target? Shim(string path) => path switch {
        $@"{Prefix}\gemini.cmd"   => new($@"{Prefix}\node.exe", $@"{Prefix}\node_modules\@google\gemini-cli\dist\index.js"),
        $@"{Prefix}\opencode.cmd" => new($@"{Prefix}\node_modules\opencode-ai\bin\opencode.exe", null),
        $@"{Prefix}\bare.cmd"     => new("node", $@"{Prefix}\bare.js"),
        _                         => null,
    };

    [Test]
    public async Task An_npm_shim_starts_its_script_under_the_bundled_node() =>
        await Assert.That(WindowsLaunchCommand.Resolve("gemini", OnPath, Shim))
            .IsEqualTo(($@"{Prefix}\node.exe", (string?)$@"{Prefix}\node_modules\@google\gemini-cli\dist\index.js"));

    [Test]
    public async Task An_npm_shim_over_a_native_binary_starts_that_binary() =>
        await Assert.That(WindowsLaunchCommand.Resolve("opencode", OnPath, Shim))
            .IsEqualTo(($@"{Prefix}\node_modules\opencode-ai\bin\opencode.exe", (string?)null));

    [Test]
    public async Task A_shim_naming_bare_node_resolves_node_on_path() =>
        await Assert.That(WindowsLaunchCommand.Resolve($@"{Prefix}\bare.cmd", p => p == "node" ? OnPath(p) : p, Shim))
            .IsEqualTo((@"C:\node\node.exe", (string?)$@"{Prefix}\bare.js"));

    [Test]
    public async Task A_cmd_that_is_not_an_npm_shim_is_started_by_its_full_path() =>
        await Assert.That(WindowsLaunchCommand.Resolve("legacy", OnPath, Shim))
            .IsEqualTo(($@"{Prefix}\legacy.cmd", (string?)null));

    [Test]
    public async Task An_exe_is_started_by_its_full_path() =>
        await Assert.That(WindowsLaunchCommand.Resolve("pi", OnPath, Shim)).IsEqualTo((@"C:\bun\pi.exe", (string?)null));

    [Test]
    public async Task A_command_that_does_not_resolve_is_left_for_CreateProcess_to_report() =>
        await Assert.That(WindowsLaunchCommand.Resolve("grok", OnPath, Shim)).IsEqualTo(("grok", (string?)null));

    [Test]
    [RunOn(OS.Windows)]
    public async Task Apply_puts_the_shim_script_ahead_of_the_vendor_arguments() {
        using var tmp = new TempDir();
        var node   = tmp.CreateFile("node.exe");
        var script = tmp.CreateFile("vendor.js");
        tmp.CreateFile("vendor.cmd", "@ECHO off\r\n\"%dp0%\\node.exe\"  \"%dp0%\\vendor.js\" %*\r\n");

        var psi = WindowsLaunchCommand.Apply(new ProcessStartInfo("vendor", ["acp", "--stdio"]), BinaryProbe.Searching(tmp.Path));

        await Assert.That(psi.FileName).IsEqualTo(node);
        await Assert.That(string.Join('|', psi.ArgumentList)).IsEqualTo($"{script}|acp|--stdio");
    }
}
