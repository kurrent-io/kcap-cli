namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Muse;

/// <summary>Lines captured from a real <c>muse serve</c> run, with absolute paths neutralised.</summary>
internal static class MuseMspWire {
    /// <summary>The <c>initialize</c> response (request id 1).</summary>
    public const string InitializeResponse =
        """{"jsonrpc":"2.0","id":1,"result":{"serverInfo":{"name":"muse","version":"1.4.7"},"userAgent":"muse-build/1.4.7 (serve; macos-aarch64; build d8c53d25bd30cda15ab32b18b9bb9961df9c4be0)","museHome":"/muse-home","platformFamily":"unix","platformOs":"macos","schema":{"version":1,"fingerprint":"sha256:6de32db08bbbe0deb478d2bfcaba001e9fb490986bc80db3aceb4d996abf7df9"},"grantedCapabilities":["sessionMcp"],"experimentalApi":true,"sessionDurability":"durable","sandboxAvailability":"available"}}""";

    /// <summary>The <c>result</c> member of <see cref="InitializeResponse"/>.</summary>
    public const string InitializeResult =
        """{"serverInfo":{"name":"muse","version":"1.4.7"},"userAgent":"muse-build/1.4.7 (serve; macos-aarch64; build d8c53d25bd30cda15ab32b18b9bb9961df9c4be0)","museHome":"/muse-home","platformFamily":"unix","platformOs":"macos","schema":{"version":1,"fingerprint":"sha256:6de32db08bbbe0deb478d2bfcaba001e9fb490986bc80db3aceb4d996abf7df9"},"grantedCapabilities":["sessionMcp"],"experimentalApi":true,"sessionDurability":"durable","sandboxAvailability":"available"}""";

    /// <summary>An <c>approval/requested</c> notification whose requirement id is an object.</summary>
    public const string ApprovalRequested =
        """{"jsonrpc":"2.0","method":"approval/requested","params":{"sessionId":"01a13695-0736-75d0-8f4e-f121585b8453","approvalId":"01a13695-6ad8-7313-9d2b-57f03aebbb24","turnId":"01a13695-08e6-743d-8b85-cffc838ef5fc","taskId":"01a13695-6ad8-7313-9d2b-57f03aebbb24","itemId":"01a13695-6ad8-7313-9d2b-57f03aebbb24","toolCallId":"call_01a1369568de712aafb33a0f87cae86a","toolName":"mcp__kcap_sessions__search_sessions","rawArgs":"{\"query\":\"muse\"}","viewCursor":"v:01a13695-0736-75d0-8f4e-f121585b8453:39","sourceRange":{"stream":{"kind":"session","id":"01a13695-0736-75d0-8f4e-f121585b8453"},"first":{"id":"1574799f-0f06-4262-8080-84400e4d7732","sequence":335},"last":{"id":"1574799f-0f06-4262-8080-84400e4d7732","sequence":335}},"subject":{"kind":"tool","toolName":"mcp__kcap_sessions__search_sessions"},"currentRequirementId":{"approvalId":"01a13695-6ad8-7313-9d2b-57f03aebbb24","sourceIndex":0},"availableChoices":[{"choiceId":"allow_once","label":"Allow once","decision":"approved","scope":"once"},{"choiceId":"allow_session","label":"Allow for this session","decision":"approvedForSession","scope":"session","rulePreview":"tool `mcp__kcap_sessions__search_sessions`"},{"choiceId":"allow_local_mcp_tool","label":"Always allow this MCP tool","decision":"approvedPolicyAmendment","scope":"localPersistent","rulePreview":"tool `mcp__kcap_sessions__search_sessions`"},{"choiceId":"abort","label":"Reject","decision":"abort","scope":"once","acceptsFeedback":true}],"protectedWrite":false,"judgeEscalated":false},"emittedAtMs":1791917124377}""";
}
