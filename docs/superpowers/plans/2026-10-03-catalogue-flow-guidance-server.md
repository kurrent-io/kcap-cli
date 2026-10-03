# Catalogue Flow Guidance — Server Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Flow definitions carry authored guidance (`offer`, `when_to_use`, `driver_guide`). The definitions listing exposes it, and a new by-id endpoint returns the driver guide.

**Architecture:**
- `guidance` is parsed into `FlowDefinitionSpec` and stored inside the existing `spec_json`, so no new column or migration is needed. It never enters the engine's `FlowDefinition`.
- `IFlowDefinitions` gains two reads that pair the engine definition with its guidance. For a built-in id whose current spec authors no guidance, they fall back to the embedded built-in YAML.

**Tech Stack:** .NET 10, YamlDotNet, EF Core (Postgres), minimal APIs, TUnit + NSubstitute, Testcontainers.

**Spec:** `docs/superpowers/specs/2026-10-03-catalogue-flow-guidance-design.md` (kcap-cli repo). Copy it onto the server branch alongside this plan.

**Repository:** `kurrent-io/kcap-server`. Run `git submodule update --init` in the fresh worktree before building, because the server test build depends on `src/cli`.

## Global Constraints

- YAML keys are snake_case; the parser rejects unknown keys, including inside `guidance`.
- `guidance.offer` ∈ {`proactive`, `on_request`}. It defaults to `on_request`.
- `guidance.when_to_use`: at most 300 characters. It is required when `offer` is `proactive`.
- `guidance.driver_guide`: markdown, at most 8192 characters.
- Guidance is metadata only. It never reaches `FlowDefinition`, the prompt renderer or a launch.
- Admin-authored YAML is never modified and no version is appended on the admin's behalf. Fallback is read-time only.
- HTTP JSON is snake_case: `offer`, `when_to_use`, `driver_guide`.
- One type per file, named after the type.
- Comments: scarce. No ticket ids, no change narration, no spec coordinates.
- Commit subject: one imperative clause, at most 80 characters. No ticket prefix unless the user supplies one.
- Every TUnit assertion is `await`ed. Filter with `--treenode-filter`.

## Review Focus

1. **A stored `spec_json` written before this change** (no `Guidance` property) must still deserialize, list, start and describe. It gets null guidance, or the built-in fallback for a built-in id. Pinned in Task 3.
2. **`guidance: {}` or a bare `guidance:` key** must parse to `on_request` with no text, or to no guidance at all, rather than throwing a null-reference error. Pinned in Task 1.
3. **A disabled or deleted built-in** must not be described through the fallback. The by-id endpoint returns 404, exactly as `start_flow` refuses it. Pinned in Task 3.
4. **Whitespace-only `when_to_use` with `offer: proactive`** must be rejected like a missing value, not stored as blank text that agents then get offered. Pinned in Task 1.
5. **`GET /api/flows/definitions/{id}` on a catalog still catching up** must answer the retryable 409 `server_catching_up`, never 404. A 404 would read as "no such flow" and stop the driver. Pinned in Task 4.

---

### Task 1: Guidance types and parser

**Files:**
- Create: `src/Capacitor.Server.Core/Flows/FlowGuidance.cs`
- Create: `src/Capacitor.Server.Core/Flows/FlowGuidanceOffer.cs`
- Modify: `src/Capacitor.Server.Services/Flows/FlowDefinitionSpec.cs:12-19` (trailing `Guidance` parameter)
- Modify: `src/Capacitor.Server.Services/Flows/FlowDefinitionYamlParser.cs` (`Validate`, `RawDefinition`, new `RawGuidance`, new `ValidateGuidance`)
- Test: `test/Capacitor.Server.Tests.Flows/FlowDefinitionYamlParserTests.cs`

**Interfaces:**
- Produces:
  - `Capacitor.Flows.FlowGuidance(string Offer, string? WhenToUse, string? DriverGuide)` with `bool IsProactive`.
  - `Capacitor.Flows.FlowGuidanceOffer.Proactive` / `.OnRequest` (string consts).
  - `FlowDefinitionSpec.Guidance` (`FlowGuidance?`, trailing, default null).
  - `FlowDefinitionYamlParser.MaxWhenToUseLength = 300`, `MaxDriverGuideLength = 8192`.

- [ ] **Step 1: Write the failing tests.** Append to `FlowDefinitionYamlParserTests`. `Valid` is the existing legacy-shape constant.

```csharp
    [Test]
    public async Task Guidance_block_is_parsed() {
        var yaml = Valid + """

            guidance:
              offer: proactive
              when_to_use: After finishing a change.
              driver_guide: |
                ## Context
                Send the commit range.
            """;

        var guidance = FlowDefinitionYamlParser.Parse(yaml).Guidance!;

        await Assert.That(guidance.Offer).IsEqualTo(FlowGuidanceOffer.Proactive);
        await Assert.That(guidance.IsProactive).IsTrue();
        await Assert.That(guidance.WhenToUse).IsEqualTo("After finishing a change.");
        await Assert.That(guidance.DriverGuide).IsEqualTo("## Context\nSend the commit range.");
    }

    [Test]
    public async Task No_guidance_block_parses_to_null() =>
        await Assert.That(FlowDefinitionYamlParser.Parse(Valid).Guidance).IsNull();

    [Test]
    public async Task An_empty_guidance_block_is_on_request_with_no_text() {
        var guidance = FlowDefinitionYamlParser.Parse(Valid + "\nguidance: {}\n").Guidance!;

        await Assert.That(guidance.Offer).IsEqualTo(FlowGuidanceOffer.OnRequest);
        await Assert.That(guidance.WhenToUse).IsNull();
        await Assert.That(guidance.DriverGuide).IsNull();
    }

    [Test]
    public async Task A_bare_guidance_key_parses_to_null() =>
        await Assert.That(FlowDefinitionYamlParser.Parse(Valid + "\nguidance:\n").Guidance).IsNull();

    [Test]
    [Arguments("guidance:\n  offer: proactive\n")]
    [Arguments("guidance:\n  offer: proactive\n  when_to_use: \"   \"\n")]
    public async Task Proactive_without_when_to_use_is_rejected(string block) =>
        await Assert.That(() => FlowDefinitionYamlParser.Parse(Valid + "\n" + block))
            .Throws<FlowDefinitionValidationException>()
            .WithMessageContaining("guidance.when_to_use is required");

    [Test]
    public async Task Unknown_offer_is_rejected() =>
        await Assert.That(() => FlowDefinitionYamlParser.Parse(Valid + "\nguidance:\n  offer: always\n"))
            .Throws<FlowDefinitionValidationException>()
            .WithMessageContaining("guidance.offer 'always'");

    [Test]
    public async Task When_to_use_over_the_cap_is_rejected() {
        var yaml = Valid + $"\nguidance:\n  when_to_use: {new string('a', FlowDefinitionYamlParser.MaxWhenToUseLength + 1)}\n";

        await Assert.That(() => FlowDefinitionYamlParser.Parse(yaml))
            .Throws<FlowDefinitionValidationException>()
            .WithMessageContaining("guidance.when_to_use must be at most 300");
    }

    [Test]
    public async Task Driver_guide_over_the_cap_is_rejected() {
        var yaml = Valid + $"\nguidance:\n  driver_guide: {new string('a', FlowDefinitionYamlParser.MaxDriverGuideLength + 1)}\n";

        await Assert.That(() => FlowDefinitionYamlParser.Parse(yaml))
            .Throws<FlowDefinitionValidationException>()
            .WithMessageContaining("guidance.driver_guide must be at most 8192");
    }

    [Test]
    public async Task Unknown_key_inside_guidance_is_rejected() =>
        await Assert.That(() => FlowDefinitionYamlParser.Parse(Valid + "\nguidance:\n  bogus_key: 1\n"))
            .Throws<FlowDefinitionValidationException>();

    [Test]
    public async Task Guidance_round_trips_through_spec_json() {
        var spec = FlowDefinitionYamlParser.Parse(Valid + "\nguidance:\n  offer: proactive\n  when_to_use: Now.\n");

        var back = FlowDefinitionSpecJson.Deserialize(FlowDefinitionSpecJson.Serialize(spec));

        await Assert.That(back.Guidance).IsEqualTo(spec.Guidance);
    }
```

If `Valid` ends without a trailing newline, the `"\n" +` prefixes keep the appended block at column 0. If `Valid` already contains a `completion:` or `mcp:` block, appending a top-level key after it is still valid YAML.

- [ ] **Step 2: Run the tests and confirm they fail.**

Run: `dotnet run --project test/Capacitor.Server.Tests.Flows/Capacitor.Server.Tests.Flows.csproj -- --treenode-filter "/*/*/FlowDefinitionYamlParserTests/*"`
Expected: compile errors, because `Guidance`, `FlowGuidanceOffer` and `MaxWhenToUseLength` do not exist.

- [ ] **Step 3: Implement.**

`src/Capacitor.Server.Core/Flows/FlowGuidanceOffer.cs`:

```csharp
namespace Capacitor.Flows;

public static class FlowGuidanceOffer {
    public const string Proactive = "proactive";
    public const string OnRequest = "on_request";
}
```

`src/Capacitor.Server.Core/Flows/FlowGuidance.cs`:

```csharp
namespace Capacitor.Flows;

/// <summary>What a driving agent is told about a definition before starting it. Catalog metadata: it never
/// reaches a launch or a participant prompt.</summary>
public sealed record FlowGuidance(string Offer, string? WhenToUse, string? DriverGuide) {
    public bool IsProactive => Offer == FlowGuidanceOffer.Proactive;
}
```

In `FlowDefinitionSpec.cs`, extend the record parameter list. Leave the rest of the file unchanged; old `spec_json` rows deserialize the missing property as null.

```csharp
public sealed record FlowDefinitionSpec(
    string                              Id,
    string?                             Description,
    IReadOnlyList<FlowParticipantEntry> Participants,
    FlowLimitsSpec?                     Limits,
    FlowCompletionSpec?                 Completion,
    IReadOnlyList<string>?              Mcp      = null,
    FlowGuidance?                       Guidance = null
) {
```

In `FlowDefinitionYamlParser.cs`:

Add the constants beside `MinParticipants`/`MaxParticipants`:

```csharp
    public const int MaxWhenToUseLength   = 300;
    public const int MaxDriverGuideLength = 8192;
```

In `Validate`, replace the last two lines (`var mcp = …; return new FlowDefinitionSpec(…);`) with:

```csharp
        var mcp      = raw.Mcp is null ? null : NormalizeMcp(raw.Mcp);
        var guidance = raw.Guidance is null ? null : ValidateGuidance(raw.Guidance);

        return new FlowDefinitionSpec(id, raw.Description, participants, limits, completion, mcp, guidance);
```

Add after `NormalizeMcp`:

```csharp
    static FlowGuidance ValidateGuidance(RawGuidance raw) {
        var offer = string.IsNullOrWhiteSpace(raw.Offer) ? FlowGuidanceOffer.OnRequest : raw.Offer.Trim();
        if (offer is not (FlowGuidanceOffer.Proactive or FlowGuidanceOffer.OnRequest))
            throw new FlowDefinitionValidationException($"guidance.offer '{raw.Offer}' must be 'proactive' or 'on_request'.");

        var whenToUse   = string.IsNullOrWhiteSpace(raw.WhenToUse) ? null : raw.WhenToUse.Trim();
        var driverGuide = string.IsNullOrWhiteSpace(raw.DriverGuide) ? null : raw.DriverGuide.TrimEnd();

        // A proactive flow is offered by its when_to_use line alone, so an offer with nothing to say is an authoring error.
        if (offer == FlowGuidanceOffer.Proactive && whenToUse is null)
            throw new FlowDefinitionValidationException("guidance.when_to_use is required when guidance.offer is 'proactive'.");
        if (whenToUse is { Length: > MaxWhenToUseLength })
            throw new FlowDefinitionValidationException($"guidance.when_to_use must be at most {MaxWhenToUseLength} characters.");
        if (driverGuide is { Length: > MaxDriverGuideLength })
            throw new FlowDefinitionValidationException($"guidance.driver_guide must be at most {MaxDriverGuideLength} characters.");

        return new FlowGuidance(offer, whenToUse, driverGuide);
    }
```

Add `public RawGuidance? Guidance { get; set; }` to `RawDefinition`. Add the nested class beside the other raw types:

```csharp
    sealed class RawGuidance   { public string? Offer { get; set; } public string? WhenToUse { get; set; } public string? DriverGuide { get; set; } }
```

- [ ] **Step 4: Run the tests and confirm they pass.**

Run the same command as in Step 2. Expected: every `FlowDefinitionYamlParserTests` test passes, including the existing ones.

- [ ] **Step 5: Commit.**

```bash
git add src/Capacitor.Server.Core/Flows/FlowGuidance.cs src/Capacitor.Server.Core/Flows/FlowGuidanceOffer.cs src/Capacitor.Server.Services/Flows/FlowDefinitionSpec.cs src/Capacitor.Server.Services/Flows/FlowDefinitionYamlParser.cs test/Capacitor.Server.Tests.Flows/FlowDefinitionYamlParserTests.cs
git commit -m "Parse an optional guidance block on flow definitions"
```

---

### Task 2: Built-in guidance

**Files:**
- Modify: `src/Capacitor.Server.Services/Flows/Catalog/BuiltIns/spec-review.yaml`
- Modify: `src/Capacitor.Server.Services/Flows/Catalog/BuiltIns/code-review.yaml`
- Modify: `src/Capacitor.Server.Services/Flows/BuiltInFlowDefinitions.cs` (summary, `GuidanceFor`)
- Test: `test/Capacitor.Server.Services.Tests/Flows/BuiltInFlowDefinitionsTests.cs`
- Test: `test/Capacitor.Server.Services.Tests/Flows/Catalog/Commands/FlowCatalogBuiltInSyncTests.cs`

**Interfaces:**
- Consumes: `FlowGuidance` and `FlowDefinitionSpec.Guidance` (Task 1).
- Produces: `BuiltInFlowDefinitions.GuidanceFor(string id) : FlowGuidance?`, which returns null for a non-built-in id.

- [ ] **Step 1: Write the failing tests.**

Append to `BuiltInFlowDefinitionsTests`:

```csharp
    [Test]
    [Arguments("spec-review")]
    [Arguments("code-review")]
    public async Task Each_built_in_is_offered_proactively_with_a_driver_guide(string id) {
        var guidance = new BuiltInFlowDefinitions().GuidanceFor(id)!;

        await Assert.That(guidance.IsProactive).IsTrue();
        await Assert.That(guidance.WhenToUse).IsNotNull();
        await Assert.That(guidance.DriverGuide!).Contains("commit range");
        await Assert.That(guidance.DriverGuide!).Contains("close_flow");
    }

    [Test]
    public async Task Only_code_review_rules_out_running_tests() {
        var shipped = new BuiltInFlowDefinitions();

        await Assert.That(shipped.GuidanceFor("code-review")!.DriverGuide!).Contains("Do not ask the reviewer to run tests");
        await Assert.That(shipped.GuidanceFor("spec-review")!.DriverGuide!).DoesNotContain("run tests");
    }

    [Test]
    public async Task A_non_built_in_id_has_no_guidance() =>
        await Assert.That(new BuiltInFlowDefinitions().GuidanceFor("my-flow")).IsNull();
```

Append to `FlowCatalogBuiltInSyncTests`. `Sync`, `Create` and `Shipped` are existing helpers in that file.

```csharp
    /// <summary>A built-in seeded before it carried guidance is republished at the next version, so the stored
    /// row gains the guidance without an operator publishing anything.</summary>
    [Test]
    public async Task A_built_in_stored_without_guidance_is_republished_with_it() {
        var (svc, store) = Create();
        var withoutGuidance = Shipped.Documents.Select(StripGuidance).ToArray();
        await Sync(svc, withoutGuidance);
        store.AppendedEvents.Clear();

        await Sync(svc);

        var events = store.AppendedEvents.Select(e => e.Payload).Cast<FlowDefinitionPublished>().ToArray();
        await Assert.That(events.Select(e => e.Version).Distinct()).IsEquivalentTo(new[] { 2 });
        await Assert.That(events.All(e => FlowDefinitionSpecJson.Deserialize(e.SpecJson).Guidance is { IsProactive: true })).IsTrue();
    }

    static string StripGuidance(string yaml) => yaml[..yaml.IndexOf("\nguidance:", StringComparison.Ordinal)] + "\n";
```

If `FakeEventStore.AppendedEvents` is not a mutable list, record its count before the second sync and `Skip(count)` instead of calling `Clear()`. `StripGuidance` relies on `guidance:` being the last top-level block in each built-in file, which Step 3 sets up.

- [ ] **Step 2: Run the tests and confirm they fail.**

Run: `dotnet run --project test/Capacitor.Server.Services.Tests/Capacitor.Server.Services.Tests.csproj -- --treenode-filter "/*/*/BuiltInFlowDefinitionsTests/*"`
Expected: compile error, because `GuidanceFor` is missing.

- [ ] **Step 3: Implement.**

Append to `spec-review.yaml` as the last top-level block:

```yaml
guidance:
  offer: proactive
  when_to_use: >-
    After you finalize a design spec or implementation plan, before implementation starts. Offer an
    independent review by a separate hosted reviewer; never start it without the user's yes. Skip it
    when the user asked you to review the spec yourself.
  driver_guide: |
    ## Before starting
    - Call `list_reviewer_vendors` and recommend a reviewer that can run for this repository, preferring a vendor other than your own harness. Pass `vendor` only for a reviewer the user named.

    ## What to submit
    - `target_kind: spec`, `target_ref`: the spec's repository-relative path, `target_title`: its title.
    - `context`: the spec path, the branch name and an explicit commit range (`<base-sha>..<head-sha>`), plus what you want scrutinised. Never `origin/main...HEAD`.
    - The reviewer works offline in a mirror of this session's project directory. Inline facts instead of linking to GitHub issues or other pages. If the spec lives in another worktree or machine, inline it or pass `mode: context-only`.

    ## Iterating
    - `findings`: address each one (revise the spec, or explain why not), commit, then `send_to_participant` with `participant: reviewer`, the new commit range and what changed per finding.
    - `clean`: call `close_flow`, then report to the user.
    - `unclear` reading `participant_died` or `participant_stopped`: resend; the fresh reviewer remembers nothing, so restate the context. `participant_parked`: resend; it resumes where it was.
    - Never start a second flow for the same spec. Recover a lost `flow_run_id` with `get_flow_status(wait: true)`.
```

Append to `code-review.yaml` as the last top-level block:

```yaml
guidance:
  offer: proactive
  when_to_use: >-
    After a code change is functionally complete: tests pass, or you are about to commit, open a PR
    or merge. Offer an independent review by a separate hosted reviewer; never start it without the
    user's yes. Skip it when the user asked you to review the code yourself, or mid-task.
  driver_guide: |
    ## Before starting
    - Call `list_reviewer_vendors` and recommend a reviewer that can run for this repository, preferring a vendor other than your own harness. Pass `vendor` only for a reviewer the user named.

    ## What to submit
    - `target_kind: pr` with the PR number as `target_ref`, or `target_kind: branch` with the branch name; `target_title`: a one-line summary.
    - `context`: the branch name and an explicit commit range (`<base-sha>..<head-sha>`), plus what changed and what to focus on. Never `origin/main...HEAD`.
    - The reviewer works offline in a mirror of this session's project directory. Inline facts instead of linking to GitHub or other pages. If the change lives in another worktree or machine, inline the diff or pass `mode: context-only`.
    - Do not ask the reviewer to run tests; CI covers test execution.

    ## Iterating
    - `findings`: address each one (fix it, or explain why not), commit, then `send_to_participant` with `participant: reviewer`, the new commit range and what changed per finding.
    - `clean`: call `close_flow`, then report to the user.
    - `unclear` reading `participant_died` or `participant_stopped`: resend; the fresh reviewer remembers nothing, so restate the context. `participant_parked`: resend; it resumes where it was.
    - Never start a second flow for the same change. Recover a lost `flow_run_id` with `get_flow_status(wait: true)`.
```

In `BuiltInFlowDefinitions.cs`, replace the class summary and add `GuidanceFor` after `RawYamlFor`:

```csharp
/// <summary>The shipped built-in definitions, loaded from embedded YAML at version 1: the catalog's seed. No flow
/// resolves from here; the one read is guidance, so an operator override of a built-in that authors none is
/// described with the shipped guidance instead of losing it.</summary>
```

```csharp
    public FlowGuidance? GuidanceFor(string id) =>
        _loaded.TryGetValue(id, out var l) ? l.Spec.Guidance : null;
```

- [ ] **Step 4: Run the tests and confirm they pass.**

Run: `dotnet run --project test/Capacitor.Server.Services.Tests/Capacitor.Server.Services.Tests.csproj -- --treenode-filter "/*/*/BuiltInFlowDefinitionsTests/*"`
Run the same command with `FlowCatalogBuiltInSyncTests`.
Expected: all tests pass. The existing sync tests compare against `Shipped`, so they pick up the new YAML.

- [ ] **Step 5: Commit.**

```bash
git add src/Capacitor.Server.Services/Flows/Catalog/BuiltIns/ src/Capacitor.Server.Services/Flows/BuiltInFlowDefinitions.cs test/Capacitor.Server.Services.Tests/Flows/BuiltInFlowDefinitionsTests.cs test/Capacitor.Server.Services.Tests/Flows/Catalog/Commands/FlowCatalogBuiltInSyncTests.cs
git commit -m "Ship driver guidance with the built-in review flows" -m "Deploying republishes spec-review and code-review at their next version unless an admin owns them."
```

---

### Task 3: Described reads with built-in fallback

**Files:**
- Create: `src/Capacitor.Server.Core/Flows/DescribedFlowDefinition.cs`
- Modify: `src/Capacitor.Server.Core/Flows/IFlowDefinitions.cs` (two methods)
- Modify: `src/Capacitor.Server.Services/Flows/FlowDefinitions.cs` (constructor, the two methods, `StoredDefinition.IsBuiltIn`)
- Modify: `test/Capacitor.Server.TestHelpers/Fakes/ShippedFlowDefinitions.cs` (implement the two methods)
- Modify: any other `IFlowDefinitions` implementer the compiler reports (check `src/Capacitor.Server/Flows/FlowDefinitionResolver.cs`)
- Test: `test/Capacitor.Server.Services.Tests/Flows/FlowDefinitionsTests.cs`

**Interfaces:**
- Consumes: `FlowGuidance`, `FlowDefinitionSpec.Guidance` (Task 1); `BuiltInFlowDefinitions.GuidanceFor` (Task 2).
- Produces:
  - `Capacitor.Flows.DescribedFlowDefinition(FlowDefinition Definition, FlowGuidance? Guidance)`
  - `IFlowDefinitions.ListRunnableDescribedAsync(CancellationToken) : Task<IReadOnlyList<DescribedFlowDefinition>>`, ordered by id, enabled and undeleted only.
  - `IFlowDefinitions.GetRunnableDescribedAsync(string id, CancellationToken) : Task<DescribedFlowDefinition>`. It throws `FlowDefinitionNotRunnableException` for an unknown, disabled, deleted or unreadable definition, and `FlowCatalogNotReadyException` when the catalog cannot catch up.

- [ ] **Step 1: Write the failing tests.**

In `FlowDefinitionsTests.Setup`, change the construction to `_catalog = new FlowDefinitions(_contexts, _barrier, new BuiltInFlowDefinitions());`.

Append the tests below. `SpecReviewYaml`, `CodeReviewYaml` and `Yaml(id)` are the file's existing helpers; `FlowCatalogSeed.Definition(rawYaml, isBuiltIn, publishedBy, …)` is the shared seed.

```csharp
    const string OverrideWithoutGuidance = """
        id: code-review
        reviewer:
          workspace: none
        rounds:
          initial_prompt: Operator review.
          follow_up_prompt: Operator follow-up.
        """;

    static string OverrideWith(string guidanceBlock) => OverrideWithoutGuidance + "\n" + guidanceBlock;

    [Test]
    public async Task An_override_of_a_built_in_without_guidance_is_described_with_the_shipped_guidance() {
        await FlowCatalogSeed.WriteAsync(_db, seed => seed.Definition(OverrideWithoutGuidance, isBuiltIn: true, publishedBy: "admin"));

        var described = await _catalog.GetRunnableDescribedAsync("code-review");

        await Assert.That(described.Guidance).IsEqualTo(new BuiltInFlowDefinitions().GuidanceFor("code-review"));
        await Assert.That(described.Definition.Participants[0].InitialPrompt).IsEqualTo("Operator review.");
    }

    [Test]
    public async Task An_override_with_its_own_guidance_is_served_as_written() {
        await FlowCatalogSeed.WriteAsync(_db, seed =>
            seed.Definition(OverrideWith("guidance:\n  offer: proactive\n  when_to_use: Operator words.\n"), isBuiltIn: true, publishedBy: "admin"));

        var described = await _catalog.GetRunnableDescribedAsync("code-review");

        await Assert.That(described.Guidance!.WhenToUse).IsEqualTo("Operator words.");
        await Assert.That(described.Guidance.DriverGuide).IsNull();
    }

    [Test]
    public async Task An_override_set_to_on_request_is_not_proactive() {
        await FlowCatalogSeed.WriteAsync(_db, seed =>
            seed.Definition(OverrideWith("guidance:\n  offer: on_request\n"), isBuiltIn: true, publishedBy: "admin"));

        var listed = await _catalog.ListRunnableDescribedAsync();

        await Assert.That(listed.Single().Guidance!.IsProactive).IsFalse();
    }

    [Test]
    public async Task A_custom_definition_without_guidance_gets_no_fallback() {
        await FlowCatalogSeed.WriteAsync(_db, seed => seed.Definition(Yaml("aaa-flow")));

        var listed = await _catalog.ListRunnableDescribedAsync();

        await Assert.That(listed.Single().Guidance).IsNull();
    }

    [Test]
    public async Task A_disabled_built_in_is_not_described() {
        await FlowCatalogSeed.WriteAsync(_db, seed => {
            var codeReview = seed.Definition(CodeReviewYaml, isBuiltIn: true);
            codeReview.SetEnabled(false, DateTimeOffset.UnixEpoch, logPosition: 2);
        });

        await Assert.That(() => _catalog.GetRunnableDescribedAsync("code-review")).Throws<FlowDefinitionNotRunnableException>();
        await Assert.That(await _catalog.ListRunnableDescribedAsync()).IsEmpty();
    }

    [Test]
    public async Task A_spec_stored_before_guidance_existed_still_describes() {
        var legacySpecJson = """{"Id":"aaa-flow","Description":null,"Participants":[{"Name":"reviewer","Vendor":null,"Model":"default","Workspace":"None","Rounds":{"InitialPrompt":"a","FollowUpPrompt":"b","Result":null}}],"Limits":null,"Completion":null,"Mcp":null}""";
        await FlowCatalogSeed.WriteAsync(_db, seed => seed.DefinitionWithSpec("aaa-flow", legacySpecJson, rawYaml: null, isBuiltIn: false, publishedBy: null));

        var described = await _catalog.GetRunnableDescribedAsync("aaa-flow");

        await Assert.That(described.Definition.Id).IsEqualTo("aaa-flow");
        await Assert.That(described.Guidance).IsNull();
    }
```

Check `DefinitionWithSpec`'s exact parameter names in `test/Capacitor.Server.TestHelpers/Persistence/FlowCatalogSeed.cs:44-59` and match them. Check how the stored `Workspace` enum is spelled by serializing a parsed spec once; it is `JsonStringEnumConverter` with the default policy, so the member name is used.

- [ ] **Step 2: Run the tests and confirm they fail.**

Run: `dotnet run --project test/Capacitor.Server.Services.Tests/Capacitor.Server.Services.Tests.csproj -- --treenode-filter "/*/*/FlowDefinitionsTests/*"` (needs a running container engine)
Expected: compile errors, because the constructor arity and the two methods are missing.

- [ ] **Step 3: Implement.**

`src/Capacitor.Server.Core/Flows/DescribedFlowDefinition.cs`:

```csharp
namespace Capacitor.Flows;

public sealed record DescribedFlowDefinition(FlowDefinition Definition, FlowGuidance? Guidance);
```

Add to `IFlowDefinitions`:

```csharp
    /// <summary><see cref="ListRunnableAsync"/>, each paired with its guidance.</summary>
    Task<IReadOnlyList<DescribedFlowDefinition>> ListRunnableDescribedAsync(CancellationToken ct = default);

    /// <summary><see cref="GetRunnableAsync"/>, paired with its guidance.</summary>
    Task<DescribedFlowDefinition> GetRunnableDescribedAsync(string id, CancellationToken ct = default);
```

In `FlowDefinitions.cs`:

```csharp
sealed class FlowDefinitions(
        IDbContextFactory<FlowsDbContext> contexts,
        IFlowCatalogProjectionBarrier     projection,
        BuiltInFlowDefinitions            builtIns
    ) : IFlowDefinitions {
```

```csharp
    public async Task<IReadOnlyList<DescribedFlowDefinition>> ListRunnableDescribedAsync(CancellationToken ct = default) {
        await CatchUpAsync(ct);

        await using var context = await contexts.CreateDbContextAsync(ct);

        var rows = await context.Definitions.Runnable()
                                .OrderBy(d => d.DefinitionId)
                                .Select(d => new { d.DefinitionId, d.Version, d.SpecJson, d.IsBuiltIn })
                                .ToListAsync(ct);

        return [..rows.Select(r => Describe(r.DefinitionId, r.Version, r.SpecJson, r.IsBuiltIn))
                      .OfType<DescribedFlowDefinition>()];
    }

    public async Task<DescribedFlowDefinition> GetRunnableDescribedAsync(string id, CancellationToken ct = default) {
        var stored = await FindAsync(id, ct);

        if (stored is null) {
            await CatchUpAsync(ct);
            stored = await FindAsync(id, ct)
                  ?? throw new FlowDefinitionNotRunnableException($"Flow definition '{id}' is not available.");
        }

        if (!stored.Runnable)
            throw new FlowDefinitionNotRunnableException($"Flow definition '{id}' is not available.");

        return Describe(id, stored.Version, stored.SpecJson, stored.IsBuiltIn)
            ?? throw new FlowDefinitionNotRunnableException($"Flow definition '{id}' has an unreadable spec.");
    }

    // IsBuiltIn survives an operator re-publish, so an override of a built-in that authors no guidance keeps the shipped one.
    DescribedFlowDefinition? Describe(string id, int version, string specJson, bool isBuiltIn) {
        var spec = FlowDefinitionSpecJson.TryDeserialize(specJson);
        if (spec is null) return null;

        return new DescribedFlowDefinition(spec.ToDefinition(version), spec.Guidance ?? (isBuiltIn ? builtIns.GuidanceFor(id) : null));
    }
```

Change `FindAsync`'s projection to `new StoredDefinition(d.Version, d.SpecJson, d.Enabled, d.DeletedAt, d.IsBuiltIn)`, and the record to:

```csharp
    record StoredDefinition(int Version, string SpecJson, bool Enabled, DateTimeOffset? DeletedAt, bool IsBuiltIn) {
        public bool Runnable => Enabled && DeletedAt is null;
    }
```

`BuiltInFlowDefinitions` is already a singleton in `FlowCatalogModule.AddFlowCatalog`, so DI resolves the new parameter.

In `ShippedFlowDefinitions`:

```csharp
    public Task<IReadOnlyList<DescribedFlowDefinition>> ListRunnableDescribedAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<DescribedFlowDefinition>>(
            [.._shipped.Definitions.Select(d => new DescribedFlowDefinition(d, _shipped.GuidanceFor(d.Id)))]);

    public Task<DescribedFlowDefinition> GetRunnableDescribedAsync(string id, CancellationToken ct = default) =>
        Task.FromResult(new DescribedFlowDefinition(Runnable(id), _shipped.GuidanceFor(id)));
```

Build the solution (`dotnet build Kurrent.Capacitor.slnx`) and implement the two methods on every other `IFlowDefinitions` implementer the compiler names. A decorator delegates both methods to its inner instance.

- [ ] **Step 4: Run the tests and confirm they pass.**

Run the Step 2 command. Expected: all `FlowDefinitionsTests` pass, the existing ones included.

- [ ] **Step 5: Commit.**

```bash
git add src/Capacitor.Server.Core/Flows/DescribedFlowDefinition.cs src/Capacitor.Server.Core/Flows/IFlowDefinitions.cs src/Capacitor.Server.Services/Flows/FlowDefinitions.cs test/
git commit -m "Describe runnable flows with their guidance"
```

---

### Task 4: Listing fields and the by-id endpoint

**Files:**
- Modify: `src/Capacitor.Api.Public.Abstractions/Flows/RunnableFlowDefinition.cs` (two trailing parameters)
- Create: `src/Capacitor.Api.Public.Abstractions/Flows/FlowDefinitionDetail.cs`
- Modify: `src/Capacitor.Api.Public/Flows/FlowEndpointHandlers.cs` (`ListDefinitions`, `Describe`, new `GetDefinition`)
- Modify: `src/Capacitor.Api.Public/Flows/FlowEndpoints.cs:87-90` (new route)
- Modify: `src/Capacitor.Server/ServerJsonContext.cs:44-62` (register the response types)
- Modify: `test/Capacitor.Server.Tests.Diagnostics/OpenApi/schemas/{minimal,dedicated,workos}/public.expected.yaml` (regenerated)
- Test: `test/Capacitor.Server.Tests.Flows/FlowEndpointsTests.cs`

**Interfaces:**
- Consumes: `IFlowDefinitions.ListRunnableDescribedAsync` / `GetRunnableDescribedAsync`, `DescribedFlowDefinition` (Task 3).
- Produces, on the wire:
  - `GET /api/flows/definitions` → each definition gains `offer` (string, always present, `on_request` when there is no guidance) and `when_to_use` (string or null).
  - `GET /api/flows/definitions/{definitionId}` → `{id, version, description, is_single_participant, participants, offer, when_to_use, driver_guide}`, or one of:
    - 404 problem with `detail`: unknown, disabled, deleted or unreadable
    - 409 `{error: "server_catching_up", …}`
    - 401

- [ ] **Step 1: Write the failing tests.** Append to `FlowEndpointsTests`.

```csharp
    static FlowDefinition Def(string id) =>
        new(id, 3, "Review code changes", [
            new FlowParticipantDefinition("reviewer", null, "default", FlowWorkspacePolicy.MirrorRequester, "initial", "follow-up")
        ]);

    static readonly FlowGuidance Guided = new(FlowGuidanceOffer.Proactive, "After a change.", "## Guide");

    [Test]
    public async Task ListDefinitions_carries_offer_and_when_to_use_but_never_the_driver_guide() {
        var definitions = Substitute.For<IFlowDefinitions>();
        definitions.ListRunnableDescribedAsync(Arg.Any<CancellationToken>())
            .Returns(new List<DescribedFlowDefinition> { new(Def("code-review"), Guided), new(Def("plain"), null) });

        var result = await Handlers(Substitute.For<IFlowOrchestratorService>(), definitions: definitions)
            .ListDefinitions(NewHttpContext(githubId: "42"), CancellationToken.None);

        var listed = (result.Result as Ok<FlowDefinitionsResponse>)!.Value!.Definitions;
        await Assert.That(listed[0].Offer).IsEqualTo("proactive");
        await Assert.That(listed[0].WhenToUse).IsEqualTo("After a change.");
        await Assert.That(listed[1].Offer).IsEqualTo("on_request");
        await Assert.That(listed[1].WhenToUse).IsNull();
    }

    [Test]
    public async Task GetDefinition_returns_the_entry_with_its_driver_guide() {
        var definitions = Substitute.For<IFlowDefinitions>();
        definitions.GetRunnableDescribedAsync("code-review", Arg.Any<CancellationToken>())
            .Returns(new DescribedFlowDefinition(Def("code-review"), Guided));

        var result = await Handlers(Substitute.For<IFlowOrchestratorService>(), definitions: definitions)
            .GetDefinition("code-review", NewHttpContext(githubId: "42"), CancellationToken.None);

        var detail = (result.Result as Ok<FlowDefinitionDetail>)!.Value!;
        await Assert.That(detail.Id).IsEqualTo("code-review");
        await Assert.That(detail.Offer).IsEqualTo("proactive");
        await Assert.That(detail.DriverGuide).IsEqualTo("## Guide");
        await Assert.That(detail.Participants.Single().Role).IsEqualTo("reviewer");
    }

    [Test]
    public async Task GetDefinition_is_404_for_a_definition_that_is_not_runnable() {
        var definitions = Substitute.For<IFlowDefinitions>();
        definitions.GetRunnableDescribedAsync("gone", Arg.Any<CancellationToken>())
            .ThrowsAsync(new FlowDefinitionNotRunnableException("Flow definition 'gone' is not available."));

        var result = await Handlers(Substitute.For<IFlowOrchestratorService>(), definitions: definitions)
            .GetDefinition("gone", NewHttpContext(githubId: "42"), CancellationToken.None);

        var problem = result.Result as ProblemHttpResult;
        await Assert.That(problem).IsNotNull();
        await Assert.That(problem!.StatusCode).IsEqualTo(StatusCodes.Status404NotFound);
        await Assert.That(problem.ProblemDetails.Detail).Contains("not available");
    }

    [Test]
    public async Task GetDefinition_returns_coded_409_when_the_catalog_has_not_projected() {
        var definitions = Substitute.For<IFlowDefinitions>();
        definitions.GetRunnableDescribedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new FlowCatalogNotReadyException("catching up"));

        var result = await Handlers(Substitute.For<IFlowOrchestratorService>(), definitions: definitions)
            .GetDefinition("code-review", NewHttpContext(githubId: "42"), CancellationToken.None);

        var refusal = result.Result as JsonHttpResult<FlowReviewerResultError>;
        await Assert.That(refusal!.StatusCode).IsEqualTo(StatusCodes.Status409Conflict);
        await Assert.That(refusal.Value!.Error).IsEqualTo(FlowErrorCodes.ServerCatchingUp);
    }

    [Test]
    public async Task GetDefinition_Unauthorized_WhenNoUser() {
        var definitions = Substitute.For<IFlowDefinitions>();

        var result = await Handlers(Substitute.For<IFlowOrchestratorService>(), definitions: definitions)
            .GetDefinition("code-review", NewHttpContext(githubId: null), CancellationToken.None);

        await Assert.That(result.Result).IsTypeOf<UnauthorizedHttpResult>();
        await definitions.DidNotReceive().GetRunnableDescribedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
```

Update the two existing `ListDefinitions_*` tests that stub `ListRunnableAsync`: stub `ListRunnableDescribedAsync` instead, wrapping each definition as `new DescribedFlowDefinition(def, null)`. Also change the `DidNotReceive()` call in `ListDefinitions_Unauthorized_WhenNoUser`.

- [ ] **Step 2: Run the tests and confirm they fail.**

Run: `dotnet run --project test/Capacitor.Server.Tests.Flows/Capacitor.Server.Tests.Flows.csproj -- --treenode-filter "/*/*/FlowEndpointsTests/*"`
Expected: compile errors (`Offer`, `FlowDefinitionDetail` and `GetDefinition` are missing).

- [ ] **Step 3: Implement.**

`RunnableFlowDefinition.cs`. Leave the existing `<param>` docs as they are; the new parameters have defaults so other constructions compile.

```csharp
public sealed record RunnableFlowDefinition(
    string                                 Id,
    int                                    Version,
    string?                                Description,
    bool                                   IsSingleParticipant,
    IReadOnlyList<RunnableFlowParticipant> Participants,
    string                                 Offer     = "on_request",
    string?                                WhenToUse = null);
```

`FlowDefinitionDetail.cs`:

```csharp
namespace Capacitor.Api.Public.Abstractions.Flows;

/// <summary>One runnable definition as a driver reads it before starting: the listing entry plus the guide for
/// driving it. Never the participants' prompts or the MCP allowlist.</summary>
public sealed record FlowDefinitionDetail(
    string                                 Id,
    int                                    Version,
    string?                                Description,
    bool                                   IsSingleParticipant,
    IReadOnlyList<RunnableFlowParticipant> Participants,
    string                                 Offer,
    string?                                WhenToUse,
    string?                                DriverGuide);
```

In `FlowEndpointHandlers.cs`, replace the body of `ListDefinitions`' lambda and the `Describe` helper, and add `GetDefinition`:

```csharp
        return await RunFlowOpAsync(ctx, async () => {
            var runnable = await definitions.ListRunnableDescribedAsync(ct);

            return new FlowDefinitionsResponse([..runnable.Select(Describe)]);
        });
    }

    /// <summary>A definition that cannot start answers 404 rather than the shared mapping's 400, so a driver can
    /// tell "no such flow" from a malformed request.</summary>
    public async Task<Results<Ok<FlowDefinitionDetail>, JsonHttpResult<FlowReviewerResultError>, ProblemHttpResult, UnauthorizedHttpResult>> GetDefinition(
            string            definitionId,
            HttpContext       ctx,
            CancellationToken ct
        ) {
        if (ctx.User.GetUserId() is null) return TypedResults.Unauthorized();

        return await RunFlowOpAsync(ctx, async () => {
            DescribedFlowDefinition described;
            try {
                described = await definitions.GetRunnableDescribedAsync(definitionId, ct);
            } catch (FlowDefinitionNotRunnableException ex) {
                throw new FlowNotFoundException(ex.Message);
            }

            var (definition, guidance) = (described.Definition, described.Guidance);

            return new FlowDefinitionDetail(
                definition.Id, definition.Version, definition.Description, definition.IsSingleParticipant,
                Participants(definition), guidance?.Offer ?? FlowGuidanceOffer.OnRequest, guidance?.WhenToUse, guidance?.DriverGuide);
        });
    }

    static RunnableFlowDefinition Describe(DescribedFlowDefinition described) =>
        new(
            described.Definition.Id,
            described.Definition.Version,
            described.Definition.Description,
            described.Definition.IsSingleParticipant,
            Participants(described.Definition),
            described.Guidance?.Offer ?? FlowGuidanceOffer.OnRequest,
            described.Guidance?.WhenToUse);

    static IReadOnlyList<RunnableFlowParticipant> Participants(FlowDefinition definition) =>
        [..definition.Participants.Select(p => new RunnableFlowParticipant(p.Name, p.Vendor, p.Model))];
```

`FlowCatalogNotReadyException` is not a `FlowDefinitionNotRunnableException`, so it escapes the inner catch and reaches `RunFlowOpAsync`'s existing 409 arm. `FlowNotFoundException` maps to 404 there. If the old `Describe(FlowDefinition)` overload has no remaining caller, delete it.

In `FlowEndpoints.cs`, directly after the `/definitions` mapping:

```csharp
            flows.MapGet("/definitions/{definitionId}", (string definitionId, HttpContext ctx, FlowEndpointHandlers h, CancellationToken ct)
                => h.GetDefinition(definitionId, ctx, ct))
                .ProducesProblems(StatusCodes.Status404NotFound)
                .ProducesJson<FlowReviewerResultError>(StatusCodes.Status409Conflict);
```

In `ServerJsonContext.cs`, add these to the `/api/flows` list:

```csharp
[JsonSerializable(typeof(FlowDefinitionsResponse))]
[JsonSerializable(typeof(FlowDefinitionDetail))]
```

- [ ] **Step 4: Run the tests and confirm they pass.**

Run the Step 2 command. Expected: all `FlowEndpointsTests` pass.

- [ ] **Step 5: Regenerate the OpenAPI snapshots.**

Run: `dotnet run --project test/Capacitor.Server.Tests.Diagnostics/Capacitor.Server.Tests.Diagnostics.csproj -- --treenode-filter "/*/*/SchemaSnapshotTests/Update_committed_schemas"`

Then run the same project without the filter. Expected: it passes, and `git diff test/Capacitor.Server.Tests.Diagnostics` shows exactly three additions:
- `offer` and `when_to_use` on `Flows_RunnableFlowDefinition`
- the new `/api/flows/definitions/{definitionId}` path
- `Flows_FlowDefinitionDetail`

- [ ] **Step 6: Commit.**

```bash
git add src/Capacitor.Api.Public.Abstractions/Flows/ src/Capacitor.Api.Public/Flows/ src/Capacitor.Server/ServerJsonContext.cs test/Capacitor.Server.Tests.Flows/FlowEndpointsTests.cs test/Capacitor.Server.Tests.Diagnostics/OpenApi/schemas/
git commit -m "Serve flow guidance on the definitions listing and by id"
```

---

### Task 5: Admin editor schema

**Files:**
- Modify: `src/Capacitor.Ui/wwwroot/flow-definition.schema.json`
- Test: `test/Capacitor.Source.Tests/Flows/FlowSchemaParityTests.cs`

**Interfaces:**
- Consumes: the built-in YAML (Task 2) and `FlowDefinitionYamlParser.MaxWhenToUseLength` / `MaxDriverGuideLength` (Task 1).

- [ ] **Step 1: Write the failing test.** Append to `FlowSchemaParityTests`. If `test/Capacitor.Source.Tests` has no YamlDotNet reference, add `<PackageReference Include="YamlDotNet" />` to its csproj; the server uses central package management, so no version is needed.

```csharp
    static string BuiltInPath(string leaf) =>
        Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(SchemaPath())!)!)!,
            "Capacitor.Server.Services", "Flows", "Catalog", "BuiltIns", leaf);

    /// <summary>The editor validates against this schema, so a shipped built-in it rejects would show an operator red
    /// squiggles on a definition the server accepts. Walks every mapping key and every <c>required</c> list.</summary>
    [Test]
    [Arguments("spec-review.yaml")]
    [Arguments("code-review.yaml")]
    public async Task Schema_accepts_every_key_of_each_built_in(string leaf) {
        using var schema = JsonDocument.Parse(await File.ReadAllTextAsync(SchemaPath()));
        var yaml = new YamlDotNet.RepresentationModel.YamlStream();
        yaml.Load(new StringReader(await File.ReadAllTextAsync(BuiltInPath(leaf))));

        var problems = new List<string>();
        Check((YamlDotNet.RepresentationModel.YamlMappingNode)yaml.Documents[0].RootNode, schema.RootElement, "", problems);

        await Assert.That(problems).IsEmpty();
    }

    static void Check(YamlDotNet.RepresentationModel.YamlMappingNode node, JsonElement schema, string path, List<string> problems) {
        if (schema.TryGetProperty("required", out var required))
            foreach (var key in required.EnumerateArray().Select(r => r.GetString()!))
                if (!node.Children.Keys.Any(k => k.ToString() == key))
                    problems.Add($"{path}{key}: required by the schema, absent in the YAML");

        foreach (var (keyNode, value) in node.Children) {
            var key = keyNode.ToString();
            JsonElement child = default;
            var known = schema.TryGetProperty("properties", out var props) && props.TryGetProperty(key, out child);
            if (!known && schema.TryGetProperty("patternProperties", out var patterns))
                foreach (var p in patterns.EnumerateObject())
                    if (System.Text.RegularExpressions.Regex.IsMatch(key, p.Name)) { child = p.Value; known = true; break; }

            if (!known) { problems.Add($"{path}{key}: not in the schema"); continue; }
            if (value is YamlDotNet.RepresentationModel.YamlMappingNode mapping) Check(mapping, child, $"{path}{key}.", problems);
        }
    }

    [Test]
    public async Task Schema_guidance_caps_match_the_parser() {
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(SchemaPath()));
        var guidance = doc.RootElement.GetProperty("properties").GetProperty("guidance").GetProperty("properties");

        await Assert.That(guidance.GetProperty("when_to_use").GetProperty("maxLength").GetInt32()).IsEqualTo(300);
        await Assert.That(guidance.GetProperty("driver_guide").GetProperty("maxLength").GetInt32()).IsEqualTo(8192);
        await Assert.That(guidance.GetProperty("offer").GetProperty("enum").EnumerateArray().Select(e => e.GetString()))
            .IsEquivalentTo(new[] { "proactive", "on_request" });
    }
```

If `SchemaPath()`'s directory walk does not land on `src/` after three parents, compute the repository root the way `SchemaPath()` does (walk up to `Kurrent.Capacitor.slnx`) and join from there instead. The 300 and 8192 here are deliberate literals: Source.Tests may not reference Services, and the parser constants are pinned by Task 1's tests.

- [ ] **Step 2: Run the tests and confirm they fail.**

Run: `dotnet run --project test/Capacitor.Source.Tests/Capacitor.Source.Tests.csproj -- --treenode-filter "/*/*/FlowSchemaParityTests/*"`
Expected: FAIL.
- `reviewer.vendor` and `reviewer.model` are required but absent.
- `rounds.initial_prompt_context_only`, `rounds.follow_up_prompt_context_only` and `guidance` are not in the schema.
- `guidance` has no caps.

- [ ] **Step 3: Implement.** Edit `flow-definition.schema.json`:

1. In the participant schema (line 18), `"required": ["vendor", "model", "rounds"]` → `"required": ["rounds"]`.
2. In `reviewer` (line 48), delete `"required": ["vendor", "model"],`.
3. In both `rounds` objects, after `"follow_up_prompt": { "type": "string" },`, add:
   ```json
                   "initial_prompt_context_only": { "type": "string", "description": "Initial prompt when the participant has no readable workspace." },
                   "follow_up_prompt_context_only": { "type": "string", "description": "Follow-up prompt when the participant has no readable workspace." },
   ```
4. After the `mcp` property (line 90), add:
   ```json
       ,
       "guidance": {
         "type": "object",
         "additionalProperties": false,
         "description": "What a driving agent is told about this flow.",
         "properties": {
           "offer": { "type": "string", "enum": ["proactive", "on_request"], "description": "proactive: agents may offer it unprompted. Default on_request." },
           "when_to_use": { "type": "string", "maxLength": 300, "description": "When to offer it. Required when offer is proactive." },
           "driver_guide": { "type": "string", "maxLength": 8192, "description": "Markdown guide for driving the flow." }
         },
         "if": { "properties": { "offer": { "const": "proactive" } }, "required": ["offer"] },
         "then": { "required": ["when_to_use"] }
       }
   ```
   Place the comma so the JSON stays valid; `Schema_file_is_well_formed_json` checks it.

- [ ] **Step 4: Run the tests and confirm they pass.**

Run the Step 2 command. Expected: all `FlowSchemaParityTests` pass.

- [ ] **Step 5: Commit.**

```bash
git add src/Capacitor.Ui/wwwroot/flow-definition.schema.json test/Capacitor.Source.Tests/
git commit -m "Accept guidance and context-only prompts in the flow editor schema"
```

---

### Task 6: Verification

- [ ] **Step 1: Build the solution.** `dotnet build Kurrent.Capacitor.slnx`. Expected: 0 warnings and 0 errors in the touched projects.
- [ ] **Step 2: Run the source scan.** `dotnet run --project test/Capacitor.Source.Tests/Capacitor.Source.Tests.csproj`. Expected: PASS.
- [ ] **Step 3: Run the flow suites.** Run each of the following:
  - `dotnet run --project test/Capacitor.Server.Tests.Flows/Capacitor.Server.Tests.Flows.csproj`
  - `dotnet run --project test/Capacitor.Server.Services.Tests/Capacitor.Server.Services.Tests.csproj`
  - `dotnet run --project test/Capacitor.Server.Tests.Diagnostics/Capacitor.Server.Tests.Diagnostics.csproj`

  Expected: all pass.
- [ ] **Step 4: Check the editor bundle.** Run `dotnet build src/Capacitor.Ui` so the NpmBuild target rebundles `flow-monaco-bundle.js` with the schema. Commit the bundle if it is tracked and changed.
