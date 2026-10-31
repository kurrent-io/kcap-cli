using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>A service manager over a fixed set of units, each naming its daemon binary, recording what it
/// was asked to do.</summary>
sealed class RecordingServiceManager(Dictionary<string, string?> binaries) : IServiceManager {
    /// <summary>A binary value whose status read throws.</summary>
    public const string Unreadable = "<unreadable>";

    /// <summary>A binary value whose supervisor does not answer a status read.</summary>
    public const string Unanswering = "<unanswering>";

    public readonly List<string> Uninstalled = [];
    public readonly List<string> Stopped     = [];
    int _statusReads;
    public int StatusReads => Volatile.Read(ref _statusReads);
    public bool ListingFails;

    /// <summary>Runs before a unit is uninstalled or stopped, as its bootout would end the daemon.</summary>
    public Action<string>? OnBootout { get; init; }

    public string Describe() => "fake";
    public string UnitDirectory => "";
    public IReadOnlyList<GeneratedFile> GenerateFiles(ServiceSpec spec) => [];
    public IReadOnlyList<string> ListInstalled() => [.. binaries.Keys];
    public IReadOnlyList<string>? TryListInstalled() => ListingFails ? null : ListInstalled();
    public IReadOnlyDictionary<string, string>? UnitEnvironment(string serviceId) => null;

    /// <summary>A unit it does not hold reads as not installed.</summary>
    public ServiceStatus Status(string serviceId) {
        Interlocked.Increment(ref _statusReads);
        if (!binaries.TryGetValue(serviceId, out var held)) return new(ServiceState.NotInstalled, null);
        return held switch {
            Unreadable  => throw new IOException("unreadable wrapper"),
            Unanswering => throw new InvalidOperationException("kcap-service is not answering on its pipe"),
            var binary  => new(ServiceState.Installed, binary),
        };
    }

    /// <summary>Units whose supervisor does not answer: their query reads Unknown.</summary>
    public HashSet<string> Unanswered { get; } = [];

    public ServiceQuery Query(string serviceId) =>
        new(Unanswered.Contains(serviceId) ? LabelProbe.Unknown : LabelProbe.Loaded, true, ServiceState.Installed, binaries[serviceId], null);
    public bool Install(ServiceSpec spec, bool startNow, out string? error) { error = null; return true; }
    public void WriteAndBootstrap(ServiceSpec spec) { }

    public bool Uninstall(string serviceId, out string? error) {
        OnBootout?.Invoke(serviceId);
        Uninstalled.Add(serviceId);
        error = null;
        return true;
    }

    public bool Start(string serviceId, out string? error) { error = null; return true; }

    public bool Stop(string serviceId, out string? error) {
        OnBootout?.Invoke(serviceId);
        Stopped.Add(serviceId);
        error = null;
        return true;
    }
}
