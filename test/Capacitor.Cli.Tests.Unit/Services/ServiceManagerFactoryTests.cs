using Capacitor.Cli.Services;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Tests.Unit.Services;

public class ServiceManagerFactoryTests {
    [TempHome] public required TempHome Home { get; init; }

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    [Test]
    public async Task ForPlatform_returns_each_concrete_manager() {
        await Assert.That(ServiceManagerFactory.ForPlatform(ServicePlatform.Launchd, Config.Root, Home, TimeProvider.System)).IsTypeOf<LaunchdServiceManager>();
        await Assert.That(ServiceManagerFactory.ForPlatform(ServicePlatform.Systemd, Config.Root, Home, TimeProvider.System)).IsTypeOf<SystemdServiceManager>();
        await Assert.That(ServiceManagerFactory.ForPlatform(ServicePlatform.WindowsScheduledTask, Config.Root, Home, TimeProvider.System)).IsTypeOf<WindowsScheduledTaskServiceManager>();
        if (OperatingSystem.IsWindows())
            await Assert.That(ServiceManagerFactory.ForPlatform(ServicePlatform.WindowsService, Config.Root, Home, TimeProvider.System)).IsTypeOf<WindowsServiceManager>();
    }

    [Test]
    public async Task The_supervisor_word_names_each_manager() {
        await Assert.That(ServiceManagerFactory.SupervisorWord(ServiceManagerFactory.ForPlatform(ServicePlatform.Launchd, Config.Root, Home, TimeProvider.System))).IsEqualTo("launchd");
        await Assert.That(ServiceManagerFactory.SupervisorWord(ServiceManagerFactory.ForPlatform(ServicePlatform.WindowsScheduledTask, Config.Root, Home, TimeProvider.System))).IsEqualTo("windows_scheduled_task");
        await Assert.That(ServiceManagerFactory.SupervisorWord(new WindowsServiceManager(Config.Root, new FakeServicePipe(), new FakeServiceEnrollmentStore(), null))).IsEqualTo("windows_service");
    }

    [Test]
    public async Task ForCurrentOs_does_not_throw_on_this_host() {
        var mgr = ServiceManagerFactory.ForCurrentOs(Config.Root, Home, TimeProvider.System, systemdRunning: () => true);
        await Assert.That(mgr.Describe()).IsNotNull();
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task ForCurrentOs_on_Linux_without_systemd_has_no_service_manager() {
        await Assert.That(() => ServiceManagerFactory.ForCurrentOs(Config.Root, Home, TimeProvider.System, systemdRunning: () => false))
            .Throws<PlatformNotSupportedException>().WithMessageContaining("kcap daemon start -d");
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task ForCurrentOs_on_Linux_with_systemd_is_the_systemd_manager() {
        await Assert.That(ServiceManagerFactory.ForCurrentOs(Config.Root, Home, TimeProvider.System, systemdRunning: () => true))
            .IsTypeOf<SystemdServiceManager>();
    }

    [Test]
    public async Task Launchd_GenerateFiles_returns_one_file() {
        var spec = new ServiceSpec("laptop", "/opt/kcap/kcap-daemon", "/tmp/daemon-laptop.log",
            new Dictionary<string, string>(), []);
        var files = ServiceManagerFactory.ForPlatform(ServicePlatform.Launchd, Config.Root, Home, TimeProvider.System).GenerateFiles(spec);
        await Assert.That(files.Count).IsEqualTo(1);
        await Assert.That(files[0].Path).EndsWith("io.kurrent.kcap.daemon.laptop.plist");
    }

    [Test]
    public async Task Windows_GenerateFiles_returns_xml_and_wrapper() {
        var spec = new ServiceSpec("laptop", @"C:\kcap\kcap-daemon.exe", @"C:\tmp\daemon-laptop.log",
            new Dictionary<string, string>(), []);
        var files = ServiceManagerFactory.ForPlatform(ServicePlatform.WindowsScheduledTask, Config.Root, Home, TimeProvider.System).GenerateFiles(spec);
        await Assert.That(files.Count).IsEqualTo(2);
    }
}
