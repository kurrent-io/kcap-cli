using System.Reflection;
using System.ServiceProcess;
using Capacitor.Cli.Service;
using Capacitor.Cli.Service.Hosting;
using Capacitor.Cli.Service.Installation;
using Capacitor.Cli.Service.Logging;

if (!OperatingSystem.IsWindows()) {
    Console.Error.WriteLine("kcap-service runs on Windows only");
    return 1;
}

var version = typeof(ServiceIdentity).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

switch (args) {
    case ["install"]:
        return ServiceInstaller.Install(Environment.ProcessPath!);
    case ["uninstall"]:
        return ServiceInstaller.Uninstall();
    case ["--version"]:
        Console.WriteLine($"kcap-service {version}");
        return 0;
    case [] when !Environment.UserInteractive:
        var logDirectory = ServiceIdentity.LogDirectory(Environment.GetEnvironmentVariable("ProgramData") ?? @"C:\ProgramData");
        ProtectedDirectory.Ensure(logDirectory);
        using (var service = new KcapService(new FileServiceLog(Path.Combine(logDirectory, "service.log"), TimeProvider.System), version)) {
            ServiceBase.Run(service);
        }
        return 0;
    default:
        Console.Error.WriteLine("usage: kcap-service install | uninstall | --version   (the service itself is started by Windows)");
        return 1;
}
