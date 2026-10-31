using System.Text.Json;

namespace Capacitor.Cli.Core.Pages;

/// A server answer to a page's response call, passed on uninterpreted. Status 0 is a transport
/// failure; Body is null when the answer carried no parseable JSON.
public sealed record PageBridgeReply(int Status, JsonElement? Body);
