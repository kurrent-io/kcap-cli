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

    /// <summary>A <c>userInput/requested</c> notification: one single-select question, options Red and Blue.</summary>
    public const string UserInputRequested =
        """{"jsonrpc":"2.0","method":"userInput/requested","params":{"sessionId":"01a13696-9235-7b32-a9e5-3d3e067625e2","userInputId":"01a13696-9fe2-7cf0-9559-4c92d2d22e4e","turnId":"01a13696-9344-7ed2-9040-df8ae372fbfc","itemId":"01a13696-9fe2-7cf0-9559-4c92d2d22e4e","toolCallId":"call_01a136969dc1778780e4116f3c3c9e6c","toolName":"request_user_input","viewCursor":"v:01a13696-9235-7b32-a9e5-3d3e067625e2:8","sourceRange":{"stream":{"kind":"session","id":"01a13696-9235-7b32-a9e5-3d3e067625e2"},"first":{"id":"5e6df77e-06ad-4a4d-aea7-a0d554abed95","sequence":59},"last":{"id":"5e6df77e-06ad-4a4d-aea7-a0d554abed95","sequence":59}},"questions":[{"id":"colour_pref","header":"Colour","question":"Which colour do you prefer?","selection":{"mode":"single"},"options":[{"label":"Red"},{"label":"Blue"}]}]},"emittedAtMs":1791917203478}""";

    /// <summary>An <c>approval/requested</c> notification for a shell command, offering only allow-once and abort.</summary>
    public const string ShellApprovalRequested =
        """{"jsonrpc":"2.0","method":"approval/requested","params":{"sessionId":"01a13696-9235-7b32-a9e5-3d3e067625e2","approvalId":"01a13696-a953-7bd0-bedc-c5bf1ef3b309","turnId":"01a13696-9344-7ed2-9040-df8ae372fbfc","taskId":"01a13696-a953-7bd0-bedc-c5bf1ef3b309","itemId":"01a13696-a953-7bd0-bedc-c5bf1ef3b309","toolCallId":"call_01a13696a650769391afaeda65128ee3","toolName":"bash","rawArgs":"{\"command\":\"echo hi > /workspace/out.txt\",\"description\":\"Write hi to probe file\"}","viewCursor":"v:01a13696-9235-7b32-a9e5-3d3e067625e2:12","sourceRange":{"stream":{"kind":"session","id":"01a13696-9235-7b32-a9e5-3d3e067625e2"},"first":{"id":"0b39c4e5-4be8-41f7-9037-fae59dbf0267","sequence":89},"last":{"id":"0b39c4e5-4be8-41f7-9037-fae59dbf0267","sequence":89}},"subject":{"kind":"shell","command":"echo hi > /workspace/out.txt","workspaceRoot":"/workspace/project","stages":[{"requirementId":{"approvalId":"01a13696-a953-7bd0-bedc-c5bf1ef3b309","sourceIndex":0},"position":1,"totalStages":1,"argv":["echo","hi"],"argvComplete":false,"resolution":{"kind":"unresolved"}}]},"currentRequirementId":{"approvalId":"01a13696-a953-7bd0-bedc-c5bf1ef3b309","sourceIndex":0},"availableChoices":[{"choiceId":"allow_once","label":"Allow once","decision":"approved","scope":"once"},{"choiceId":"abort","label":"Reject","decision":"abort","scope":"once","acceptsFeedback":true}],"protectedWrite":false,"judgeEscalated":false},"emittedAtMs":1791917205894}""";

    /// <summary>The session id every captured session-one line carries.</summary>
    public const string WireSessionId = "01a13695-0736-75d0-8f4e-f121585b8453";

    /// <summary>The turn id the captured <c>turn/started</c> and <c>turn/completed</c> carry.</summary>
    public const string WireTurnId = "01a13695-08e6-743d-8b85-cffc838ef5fc";

    /// <summary>The <c>result</c> of the <c>session/start</c> response, session id <see cref="WireSessionId"/>.</summary>
    public const string SessionStartResult =
        """{"session":{"sessionId":"01a13695-0736-75d0-8f4e-f121585b8453","path":"/muse-home/sessions/2026/10/13/01a13695-0736-75d0-8f4e-f121585b8453/session.jsonl","status":"idle","activeTurnId":null,"createdAt":"2026-10-13T18:44:58.835129Z","updatedAt":"2026-10-13T18:44:58.835133Z","workspaceRoot":"/workspace/project","providerId":"meta","modelId":"muse-spark-1.3","turnCount":0,"forkedFrom":null,"sideFrom":null,"approvalMode":{"mode":"onRequest","source":"startup","lastCommandId":null},"createdByClient":{"name":"kcap_probe","version":"0.0.1"},"kind":"root","permissionState":{"profileId":":read-only","reviewerAuthority":"human","reviewerAttached":true}},"viewCursor":"v:01a13695-0736-75d0-8f4e-f121585b8453:1"}""";

    /// <summary>A <c>turn/started</c> notification for <see cref="WireTurnId"/>.</summary>
    public const string TurnStarted =
        """{"jsonrpc":"2.0","method":"turn/started","params":{"sessionId":"01a13695-0736-75d0-8f4e-f121585b8453","viewCursor":"v:01a13695-0736-75d0-8f4e-f121585b8453:3","sourceRange":{"stream":{"kind":"session","id":"01a13695-0736-75d0-8f4e-f121585b8453"},"first":{"id":"ae11effd-0271-4a3c-89f4-efc5caa94f26","sequence":25},"last":{"id":"ae11effd-0271-4a3c-89f4-efc5caa94f26","sequence":25}},"turnId":"01a13695-08e6-743d-8b85-cffc838ef5fc","commandId":"01a13695-08e6-743d-8b85-cffc838ef5fc"},"emittedAtMs":1791917099344}""";

    /// <summary>A <c>session/tokenUsage</c> notification: 35873 input, 259 output, 166 reasoning tokens.</summary>
    public const string SessionTokenUsage =
        """{"jsonrpc":"2.0","method":"session/tokenUsage","params":{"sessionId":"01a13695-0736-75d0-8f4e-f121585b8453","viewCursor":"v:01a13695-0736-75d0-8f4e-f121585b8453:6","sourceRange":{"stream":{"kind":"session","id":"01a13695-0736-75d0-8f4e-f121585b8453"},"first":{"id":"27919f6a-8f90-4fe3-9e4f-76d2c7378b9e","sequence":65},"last":{"id":"27919f6a-8f90-4fe3-9e4f-76d2c7378b9e","sequence":65}},"turnId":"01a13695-08e6-743d-8b85-cffc838ef5fc","modelId":"muse-spark-1.3","usage":{"inputTokens":35873,"outputTokens":259,"cachedTokens":0,"cacheWriteTokens":0,"cacheReadTokens":0,"reasoningTokens":166},"promptTokens":35873,"totalTokens":36132,"finishReason":"tool_calls","durationMs":3232,"cumulative":{"promptTokens":35873,"outputTokens":259,"totalTokens":36132}},"emittedAtMs":1791917102839}""";

    /// <summary>A <c>session/statusChanged</c> notification to <c>idle</c>.</summary>
    public const string StatusIdle =
        """{"jsonrpc":"2.0","method":"session/statusChanged","params":{"sessionId":"01a13695-0736-75d0-8f4e-f121585b8453","status":"idle","permissionState":{"profileId":":read-only","reviewerAuthority":"human","reviewerAttached":true},"viewCursor":"v:01a13695-0736-75d0-8f4e-f121585b8453:50"},"emittedAtMs":1791917137754}""";

    /// <summary>A <c>turn/completed</c> notification for <see cref="WireTurnId"/> with terminal <c>completed</c>.</summary>
    public const string TurnCompleted =
        """{"jsonrpc":"2.0","method":"turn/completed","params":{"sessionId":"01a13695-0736-75d0-8f4e-f121585b8453","viewCursor":"v:01a13695-0736-75d0-8f4e-f121585b8453:50","sourceRange":{"stream":{"kind":"session","id":"01a13695-0736-75d0-8f4e-f121585b8453"},"first":{"id":"14304690-3679-47db-9e3e-95c84db08368","sequence":387},"last":{"id":"14304690-3679-47db-9e3e-95c84db08368","sequence":387}},"turnId":"01a13695-08e6-743d-8b85-cffc838ef5fc","terminal":"completed","durationMs":38428,"timeToFirstTokenMs":27150},"emittedAtMs":1791917137756}""";

    /// <summary>A <c>session/closed</c> notification, reason <c>hostShutdown</c>.</summary>
    public const string SessionClosed =
        """{"jsonrpc":"2.0","method":"session/closed","params":{"sessionId":"01a13695-0736-75d0-8f4e-f121585b8453","reason":"hostShutdown","viewCursor":"v:01a13695-0736-75d0-8f4e-f121585b8453:51"},"emittedAtMs":1791917198979}""";
}
