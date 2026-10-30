using Capacitor.Remote.Models;

namespace Capacitor.App.Services;

public sealed record SearchFetch(ServerReadOutcome Outcome, SearchResponseDto? Response);
