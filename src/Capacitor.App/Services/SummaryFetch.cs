using Capacitor.Remote.Models;

namespace Capacitor.App.Services;

public sealed record SummaryFetch(ServerReadOutcome Outcome, ActivitySessionDto? Session);
