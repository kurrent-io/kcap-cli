# Desktop Glass Material Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship the liquid glass material study as a production setting of the desktop app: Opaque, Soft glass or Liquid glass, chosen in Settings, applied live to the rail, the launcher's goal card and chips, and the panel flyouts.

**Architecture:** An inherited attached property, `MaterialScope.Material`, carries the material down the logical tree; styles select on it. A `Surface` control replaces the inline card `Border`s outside the chat view and swaps its template between an opaque `Border` and a glass template. All glass drawing sits in one `GlassLayer` control that wraps the vendored `LiquidGlassSurface`. A `MaterialService` resolves the effective material from the stored choice, the platform, macOS "Reduce transparency" and a latched pipeline failure.

**Tech Stack:** .NET 10, Avalonia 12.1.2, ReactiveUI, SkiaSharp 3.119.4, TUnit on Microsoft Testing Platform, LiquidGlassAvaloniaUI v0.2.0 (vendored source, MIT).

**Spec:** `docs/superpowers/specs/2026-09-18-desktop-glass-material-design.md`

## Global Constraints

- Branch `alexeyzimarev/desktop-glass-material`, cut from main. The prototype's files are read with `git show origin/prototype/desktop-liquid-glass:<path>`; nothing from `src/Capacitor.App/Prototypes/` is added.
- Avalonia stays at 12.1.2. New central pins: `Avalonia.Skia` 12.1.2, `SkiaSharp` 3.119.4.
- Files under `src/ThirdParty/LiquidGlassAvaloniaUI/` stay byte-identical to upstream tag `v0.2.0` (commit `0c65bf0aadc32d50eb0d83215c48f4222f9af84b`) except the patches listed in its `VENDORED.md`. Repo rules for comments, one type per file and namespaces do not apply there.
- Everywhere else: one type per file named after the type; namespace follows the directory; no `InternalsVisibleTo` on a new project; comments only where they name a trap or a deliberate decision, never history.
- The build is warnings-as-errors. After each task `dotnet build Capacitor.slnx` must finish with 0 warnings and 0 errors; a single-project build misses XAML warnings and cross-project breaks.
- UI tests carry `[NotInParallel("AvaloniaSession")]` and run their body through `AvaloniaSession.RunOnUiAsync` or `DispatchAsync`. The suite's application uses the dummy drawing backend: tests assert structure, never pixels.
- Tests never read the platform. They pass `MaterialEnvironment` explicitly.
- Run one test class: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/<ClassName>/*"`. `--filter` matches nothing.
- Git runs as `/usr/bin/git -C <worktree> …`, one plain command at a time. Commit subjects are one imperative clause of at most 80 characters with no issue reference (none exists yet), and every message ends with `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`.
- XAML namespaces used throughout: `xmlns:kcap="clr-namespace:Capacitor.App.Controls"`, `xmlns:glass="clr-namespace:LiquidGlassAvaloniaUI;assembly=LiquidGlassAvaloniaUI"`.
- The glass style files are included at the END of `Application.Styles` in `App.axaml`, after every inline style, in this order: `GlassStyles`, `SurfaceStyles`, `GlassChipStyles`, `GlassRailStyles`, `GlassFlyoutStyles`. Later-declared styles win at equal priority, and they must beat `App.axaml`'s own `kcapChip` and `kcapPanel` styles.
- "Any glass" is always two comma-separated selector arms, one with `[(kcap|MaterialScope.Material)=SoftGlass]` and one with `[(kcap|MaterialScope.Material)=LiquidGlass]`. Avalonia 12.1.2's XAML selector grammar rejects a `:not(…)` wrapped around a property match (AVLN2201, "Expected an identifier, got '['"). Every any-glass style must carry BOTH arms: a missed arm silently leaves Liquid glass on the opaque look.

## Proven before planning

A scratch spike against unmodified v0.2.0 source settled these, so no task re-litigates them:

- the source compiles on `net10.0`, Avalonia 12.1.2 and SkiaSharp 3.119.4 with `LangVersion` 9 and no warnings;
- a `LiquidGlassSurface` attaches and renders ten ticks under the dummy headless backend without throwing;
- a style selecting on an inherited attached property matches descendants and re-evaluates on change (the spike built its selectors in code, where `Not(…)` works; the XAML grammar does not accept that form, see Global Constraints);
- the inherited value reaches `FlyoutPresenter`, `MenuFlyoutPresenter` and flyout content, and `Popup.ShouldUseOverlayLayer = true` yields `IsUsingOverlayLayer == true`;
- glass in an overlay-layer `Popup` blurs the window behind it (stripe-edge contrast 10.8 → 0.4);
- foreground drawn as a sibling of the glass is ghosted under itself (1360 px) unless an ancestor sets `LiquidGlassBackdrop.IsExcludedFromCapture` (0 px).

## File Structure

| Path | Responsibility |
|---|---|
| `src/ThirdParty/Directory.Build.props` | Stops the root props import for vendored code |
| `src/ThirdParty/LiquidGlassAvaloniaUI/` | Upstream source, `LICENSE`, `VENDORED.md`, our `.csproj`, the `LiquidGlassPipeline` patch |
| `src/Capacitor.App/Materials/SurfaceMaterial.cs` | The enum and its stored-string mapping |
| `src/Capacitor.App/Materials/MaterialAvailability.cs` | Why glass is or is not on offer |
| `src/Capacitor.App/Materials/MaterialState.cs` | The one snapshot consumers read |
| `src/Capacitor.App/Materials/MaterialEnvironment.cs` | Platform capability and the accessibility flag |
| `src/Capacitor.App/Materials/IMaterialService.cs`, `MaterialService.cs` | Resolution, persistence, failure latch |
| `src/Capacitor.App/Materials/MaterialPipelineWatch.cs` | Turns the library's failure event into one service report |
| `src/Capacitor.App/Views/AppKitAccessibility.cs` | Reads macOS "Reduce transparency" |
| `src/Capacitor.App/Controls/MaterialScope.cs` | The inherited attached property |
| `src/Capacitor.App/Controls/GlassKind.cs`, `GlassLayer.cs`, `GlassLayer.axaml`, `GlassStyles.axaml` | The one glass drawing control and every glass parameter |
| `src/Capacitor.App/Controls/Surface.cs`, `Surface.axaml`, `SurfaceStyles.axaml` | The card control, its opaque theme, its glass template |
| `src/Capacitor.App/Controls/GlassChipStyles.axaml` | The launcher chips under glass |
| `src/Capacitor.App/Controls/GlassRailStyles.axaml` | The rail's floating layout and row fills under glass |
| `src/Capacitor.App/Controls/MaterialBackdrop.cs` | The glows behind both panes |
| `src/Capacitor.App/Controls/GlassFlyouts.cs`, `GlassFlyoutStyles.axaml` | Overlay-layer rule and the two presenter templates |
| `docs/probes/2026-09-18-glass-overlay-flyout/` | The render probe and its findings |

---

### Task 1: Vendor LiquidGlassAvaloniaUI

**Files:**
- Create: `src/ThirdParty/Directory.Build.props`
- Create: `src/ThirdParty/LiquidGlassAvaloniaUI/` (13 `.cs`, `Assets/Shaders/*.sksl`, `LICENSE`, `LiquidGlassAvaloniaUI.csproj`, `VENDORED.md`, `LiquidGlassPipeline.cs`)
- Modify: `src/ThirdParty/LiquidGlassAvaloniaUI/LiquidGlassDrawOperation.cs` (two failure paths)
- Modify: `Directory.Packages.props`, `Capacitor.slnx`, `src/Capacitor.App/Capacitor.App.csproj`, `CLAUDE.md`
- Test: `test/Capacitor.App.Tests.Unit/VendoredGlassTests.cs`

**Interfaces:**
- Produces: assembly `LiquidGlassAvaloniaUI` with `LiquidGlassSurface`, `LiquidGlassBackdrop.IsExcludedFromCapture`, and `public static class LiquidGlassPipeline { public static event Action<string>? Unavailable; }`.

- [ ] **Step 1: Fetch upstream at the pinned tag**

Run each line on its own, with `<scratch>` a throwaway directory outside the repo:

```bash
gh api repos/KaranocaVe/LiquidGlassAvaloniaUI/tarball/v0.2.0 > <scratch>/liquidglass.tgz
tar -xzf <scratch>/liquidglass.tgz -C <scratch>
ls <scratch>
```

Expected: a directory named exactly `KaranocaVe-LiquidGlassAvaloniaUI-0c65bf0`. Any other suffix means the tag moved: stop and report.

- [ ] **Step 2: Copy the library directory and the licence**

```bash
mkdir -p src/ThirdParty
cp -R <scratch>/KaranocaVe-LiquidGlassAvaloniaUI-0c65bf0/LiquidGlassAvaloniaUI src/ThirdParty/LiquidGlassAvaloniaUI
cp <scratch>/KaranocaVe-LiquidGlassAvaloniaUI-0c65bf0/LICENSE src/ThirdParty/LiquidGlassAvaloniaUI/LICENSE
rm src/ThirdParty/LiquidGlassAvaloniaUI/LiquidGlassAvaloniaUI.csproj
```

Delete any `bin/` or `obj/` that came along. Expected contents: 13 `.cs` files, `Assets/Shaders/` with 6 `.sksl` files, `LICENSE`.

- [ ] **Step 3: Write the props file that stops the root import**

`src/ThirdParty/Directory.Build.props`:

```xml
<Project>
    <!-- Deliberately does not import the repo-root file: vendored source stays byte-identical to
         upstream, so it cannot meet warnings-as-errors, enforced code style or the banned-API list.
         The analyzers are off as well: the root .editorconfig reaches this directory by ancestry,
         not by import, and would raise its CA severities on unmodified upstream code. -->
    <PropertyGroup>
        <Deterministic>true</Deterministic>
        <EnableNETAnalyzers>false</EnableNETAnalyzers>
    </PropertyGroup>
</Project>
```

- [ ] **Step 4: Write the project file**

`src/ThirdParty/LiquidGlassAvaloniaUI/LiquidGlassAvaloniaUI.csproj`. The file name fixes the assembly name, which the library's own `avares://LiquidGlassAvaloniaUI/Assets/Shaders/…` URIs depend on.

```xml
<Project Sdk="Microsoft.NET.Sdk">
    <PropertyGroup>
        <TargetFramework>net10.0</TargetFramework>
        <LangVersion>9</LangVersion>
        <Nullable>enable</Nullable>
        <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
        <IsPackable>false</IsPackable>
    </PropertyGroup>
    <ItemGroup>
        <PackageReference Include="Avalonia" />
        <PackageReference Include="Avalonia.Skia" />
        <PackageReference Include="SkiaSharp" />
    </ItemGroup>
    <ItemGroup>
        <AvaloniaResource Include="Assets\Shaders\*.sksl" />
    </ItemGroup>
</Project>
```

- [ ] **Step 5: Add the central pins, the solution entry and the app reference**

In `Directory.Packages.props`, beside the other Avalonia entries:

```xml
    <PackageVersion Include="Avalonia.Skia" Version="12.1.2" />
    <PackageVersion Include="SkiaSharp" Version="3.119.4" />
```

In `Capacitor.slnx`, directly after the `src\Capacitor.App\Capacitor.App.csproj` line:

```xml
    <Project Path="src\ThirdParty\LiquidGlassAvaloniaUI\LiquidGlassAvaloniaUI.csproj" />
```

In `src/Capacitor.App/Capacitor.App.csproj`, in the first `ItemGroup`, after the `Capacitor.Remote.Models` reference:

```xml
        <ProjectReference Include="..\ThirdParty\LiquidGlassAvaloniaUI\LiquidGlassAvaloniaUI.csproj" />
```

- [ ] **Step 6: Write the failing tests**

`test/Capacitor.App.Tests.Unit/VendoredGlassTests.cs`:

```csharp
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using LiquidGlassAvaloniaUI;

namespace Capacitor.App.Tests.Unit;

public class VendoredGlassTests {
    /// Pins the non-public member the vendored backdrop provider reflects on. If an Avalonia bump
    /// renames it, the lookup returns null and the backdrop silently stops refreshing.
    [Test]
    public async Task TopLevel_still_has_the_renderer_property_the_backdrop_provider_reflects_on() {
        var property = typeof(TopLevel).GetProperty(
            "Renderer", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        await Assert.That(property).IsNotNull();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_glass_surface_renders_under_the_headless_backend() => AvaloniaSession.RunOnUiAsync(async () => {
        var window = new Window { Width = 400, Height = 300, Content = new LiquidGlassSurface { Width = 200, Height = 120 } };
        try {
            window.Show();
            for (var i = 0; i < 3; i++) {
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            }
            await Assert.That(window.IsVisible).IsTrue();
        } finally {
            window.Close();
        }
    });

    [Test]
    public async Task The_pipeline_reports_unavailability_once() {
        var reasons = new List<string>();
        void Handler(string reason) => reasons.Add(reason);
        LiquidGlassPipeline.Unavailable += Handler;
        try {
            LiquidGlassPipeline.Report("first");
            LiquidGlassPipeline.Report("second");
        } finally {
            LiquidGlassPipeline.Unavailable -= Handler;
        }
        await Assert.That(reasons.Count).IsLessThanOrEqualTo(1);
    }
}
```

The last test tolerates zero reports because the flag is process-wide and the headless backend, which has no Skia lease, may already have tripped it.

- [ ] **Step 7: Run to verify failure**

Run: `dotnet build Capacitor.slnx`
Expected: FAIL, `LiquidGlassPipeline` does not exist.

- [ ] **Step 8: Add the patch**

`src/ThirdParty/LiquidGlassAvaloniaUI/LiquidGlassPipeline.cs` (block namespace: the project compiles as C# 9):

```csharp
using System;
using System.Threading;

namespace LiquidGlassAvaloniaUI
{
    // Local addition, not upstream: see VENDORED.md.
    public static class LiquidGlassPipeline
    {
        private static int s_reported;

        public static event Action<string>? Unavailable;

        public static void Report(string reason)
        {
            if (Interlocked.Exchange(ref s_reported, 1) == 0)
                Unavailable?.Invoke(reason);
        }
    }
}
```

In `LiquidGlassDrawOperation.cs`, inside `Render`, replace

```csharp
            if (leaseFeature is null)
                return;
```

with

```csharp
            if (leaseFeature is null)
            {
                LiquidGlassPipeline.Report("the Skia renderer is not active");
                return;
            }
```

and in the shader loader replace

```csharp
                if (effect == null)
                    Console.WriteLine($"[LiquidGlass] Failed to create SKRuntimeEffect ({assetUriString}): {errorText}");
```

with

```csharp
                if (effect == null)
                {
                    Console.WriteLine($"[LiquidGlass] Failed to create SKRuntimeEffect ({assetUriString}): {errorText}");
                    LiquidGlassPipeline.Report($"shader {assetUriString} did not compile: {errorText}");
                }
```

In the `catch (Exception ex)` of the same method, add as its first statement:

```csharp
                LiquidGlassPipeline.Report($"shader {assetUriString} could not be loaded: {ex.Message}");
```

- [ ] **Step 9: Write `VENDORED.md`**

`src/ThirdParty/LiquidGlassAvaloniaUI/VENDORED.md`:

```markdown
# LiquidGlassAvaloniaUI

- Source: https://github.com/KaranocaVe/LiquidGlassAvaloniaUI
- Tag: `v0.2.0`
- Commit: `0c65bf0aadc32d50eb0d83215c48f4222f9af84b`
- Licence: MIT, see `LICENSE`
- Copied: the `LiquidGlassAvaloniaUI/` library directory only. The demo, browser and test
  projects are not vendored. The package is not published on NuGet.org.

Every file is byte-identical to upstream except the patches below. This directory is exempt
from the repo's comment, one-type-per-file and namespace rules, and from warnings-as-errors,
through `src/ThirdParty/Directory.Build.props`.

## Local files

- `LiquidGlassAvaloniaUI.csproj` — targets `net10.0` and takes package versions from the
  repo's central pins. Upstream targets `net8.0` on Avalonia 12.0.1.
- `LiquidGlassPipeline.cs` — new. A public `Unavailable` event raised once per process.

## Patches to upstream files

- `LiquidGlassDrawOperation.cs` — upstream reports nothing when the pipeline cannot run: a
  missing Skia lease returns silently and a shader that fails to compile is only written to
  the console. Both paths, and the shader-load `catch`, now also call
  `LiquidGlassPipeline.Report`, so the app can fall back to the opaque material.

## Updating

Diff the new tag's `LiquidGlassAvaloniaUI/` against this directory, re-apply the patches
above, and update the tag and commit here. `VendoredGlassTests` pins the non-public
`TopLevel.Renderer` lookup the backdrop provider depends on.
```

- [ ] **Step 10: Note the directory in `CLAUDE.md`**

Append to the `**File paths:**` sentence at the top of `CLAUDE.md`: `, vendored third-party source at `src/ThirdParty/` (exempt from this file's code rules; each directory's `VENDORED.md` lists its local patches)`.

- [ ] **Step 11: Build and run the tests**

Run: `dotnet build Capacitor.slnx`
Expected: 0 warnings, 0 errors.

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/VendoredGlassTests/*"`
Expected: 3 passed.

- [ ] **Step 12: Commit**

```bash
/usr/bin/git -C <worktree> add src/ThirdParty Directory.Packages.props Capacitor.slnx src/Capacitor.App/Capacitor.App.csproj CLAUDE.md test/Capacitor.App.Tests.Unit/VendoredGlassTests.cs
/usr/bin/git -C <worktree> commit -m "Vendor the LiquidGlassAvaloniaUI source" -m "The package is not on NuGet.org, and upstream reports no pipeline failure, so the copy carries one event the app can fall back on." -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 2: Material model and service

**Files:**
- Create: `src/Capacitor.App/Materials/SurfaceMaterial.cs`, `MaterialAvailability.cs`, `MaterialState.cs`, `MaterialEnvironment.cs`, `IMaterialService.cs`, `MaterialService.cs`
- Modify: `src/Capacitor.App/Services/AppStateStore.cs` (the `AppState` record)
- Test: `test/Capacitor.App.Tests.Unit/InMemoryAppStateStore.cs`, `MaterialServiceTests.cs`, and `AppStateStoreTests.cs` (two new tests)

**Interfaces:**
- Produces:
  - `enum SurfaceMaterial { Opaque, SoftGlass, LiquidGlass }`; `SurfaceMaterials.ToStored(this SurfaceMaterial) : string`; `SurfaceMaterials.Parse(string?) : SurfaceMaterial?`; `SurfaceMaterials.IsGlass(this SurfaceMaterial) : bool`
  - `enum MaterialAvailability { Available, NotCapable, PipelineFailed }`
  - `record MaterialState(SurfaceMaterial Effective, SurfaceMaterial? Requested, MaterialAvailability Availability, string? FailureReason, bool ReduceTransparency)` with `static MaterialState Opaque`
  - `record MaterialEnvironment(bool GlassCapable, bool ReduceTransparency)`
  - `IMaterialService { MaterialState Current; IObservable<MaterialState> States; Task SetAsync(SurfaceMaterial); void ReportPipelineFailure(string) }`
  - `MaterialService(IAppStateStore, MaterialEnvironment, SurfaceMaterial? requested)` and `static Task<MaterialService> LoadAsync(IAppStateStore, MaterialEnvironment)`
  - `AppState.Material : string?`

- [ ] **Step 1: Write the failing tests**

`test/Capacitor.App.Tests.Unit/InMemoryAppStateStore.cs`:

```csharp
using Capacitor.App.Services;

namespace Capacitor.App.Tests.Unit;

public sealed class InMemoryAppStateStore(AppState? initial = null, bool writesSucceed = true) : IAppStateStore {
    public AppState State { get; private set; } = initial ?? new AppState();

    public Task<AppState> LoadAsync() => Task.FromResult(State);

    public Task<bool> UpdateAsync(Func<AppState, AppState> mutate) {
        if (writesSucceed) State = mutate(State);
        return Task.FromResult(writesSucceed);
    }
}
```

`test/Capacitor.App.Tests.Unit/MaterialServiceTests.cs`:

```csharp
using Capacitor.App.Materials;
using Capacitor.App.Services;

namespace Capacitor.App.Tests.Unit;

public class MaterialServiceTests {
    static readonly MaterialEnvironment Mac = new(GlassCapable: true, ReduceTransparency: false);

    [Test]
    public async Task No_choice_on_a_capable_machine_is_soft_glass() {
        var service = new MaterialService(new InMemoryAppStateStore(), Mac, requested: null);
        await Assert.That(service.Current).IsEqualTo(
            new MaterialState(SurfaceMaterial.SoftGlass, null, MaterialAvailability.Available, null, false));
    }

    [Test]
    public async Task No_choice_with_reduce_transparency_is_opaque() {
        var service = new MaterialService(new InMemoryAppStateStore(), Mac with { ReduceTransparency = true }, requested: null);
        await Assert.That(service.Current.Effective).IsEqualTo(SurfaceMaterial.Opaque);
        await Assert.That(service.Current.ReduceTransparency).IsTrue();
    }

    [Test]
    public async Task An_explicit_glass_choice_beats_reduce_transparency() {
        var service = new MaterialService(new InMemoryAppStateStore(), Mac with { ReduceTransparency = true }, SurfaceMaterial.LiquidGlass);
        await Assert.That(service.Current.Effective).IsEqualTo(SurfaceMaterial.LiquidGlass);
    }

    [Test]
    public async Task A_machine_that_cannot_do_glass_is_opaque_and_keeps_the_request() {
        var service = new MaterialService(new InMemoryAppStateStore(), new MaterialEnvironment(false, false), SurfaceMaterial.SoftGlass);
        await Assert.That(service.Current.Effective).IsEqualTo(SurfaceMaterial.Opaque);
        await Assert.That(service.Current.Requested).IsEqualTo(SurfaceMaterial.SoftGlass);
        await Assert.That(service.Current.Availability).IsEqualTo(MaterialAvailability.NotCapable);
    }

    [Test]
    public async Task Set_persists_the_stored_name_and_publishes() {
        var store = new InMemoryAppStateStore();
        var service = new MaterialService(store, Mac, requested: null);
        var seen = new List<MaterialState>();
        using var _ = service.States.Subscribe(seen.Add);

        await service.SetAsync(SurfaceMaterial.LiquidGlass);

        await Assert.That(store.State.Material).IsEqualTo("liquid_glass");
        await Assert.That(seen[^1].Effective).IsEqualTo(SurfaceMaterial.LiquidGlass);
        await Assert.That(seen[^1].Requested).IsEqualTo(SurfaceMaterial.LiquidGlass);
    }

    [Test]
    public async Task A_failed_write_still_applies_for_the_run() {
        var service = new MaterialService(new InMemoryAppStateStore(writesSucceed: false), Mac, requested: null);
        await service.SetAsync(SurfaceMaterial.Opaque);
        await Assert.That(service.Current.Effective).IsEqualTo(SurfaceMaterial.Opaque);
    }

    [Test]
    public async Task A_pipeline_failure_latches_opaque_with_its_reason_and_keeps_the_request() {
        var service = new MaterialService(new InMemoryAppStateStore(), Mac, SurfaceMaterial.SoftGlass);
        service.ReportPipelineFailure("shader did not compile");
        service.ReportPipelineFailure("a later, ignored reason");

        await Assert.That(service.Current).IsEqualTo(new MaterialState(
            SurfaceMaterial.Opaque, SurfaceMaterial.SoftGlass, MaterialAvailability.PipelineFailed, "shader did not compile", false));
    }

    [Test]
    public async Task A_failure_publishes_even_when_the_effective_material_does_not_move() {
        var service = new MaterialService(new InMemoryAppStateStore(), Mac, SurfaceMaterial.Opaque);
        var seen = new List<MaterialState>();
        using var _ = service.States.Subscribe(seen.Add);

        service.ReportPipelineFailure("no lease");

        await Assert.That(seen.Count).IsEqualTo(2);
        await Assert.That(seen[1].Availability).IsEqualTo(MaterialAvailability.PipelineFailed);
    }

    [Test]
    public async Task A_late_subscriber_gets_the_current_state() {
        var service = new MaterialService(new InMemoryAppStateStore(), Mac, requested: null);
        service.ReportPipelineFailure("no lease");
        MaterialState? first = null;
        using var _ = service.States.Subscribe(s => first ??= s);
        await Assert.That(first!.Availability).IsEqualTo(MaterialAvailability.PipelineFailed);
    }

    [Test]
    public async Task Load_reads_the_stored_choice_leniently() {
        var known = await MaterialService.LoadAsync(new InMemoryAppStateStore(new AppState(Material: "opaque")), Mac);
        var unknown = await MaterialService.LoadAsync(new InMemoryAppStateStore(new AppState(Material: "frosted_titanium")), Mac);
        await Assert.That(known.Current.Requested).IsEqualTo(SurfaceMaterial.Opaque);
        await Assert.That(unknown.Current.Requested).IsNull();
        await Assert.That(unknown.Current.Effective).IsEqualTo(SurfaceMaterial.SoftGlass);
    }
}
```

Add to `test/Capacitor.App.Tests.Unit/AppStateStoreTests.cs`:

```csharp
    [Test]
    public async Task Material_round_trips() {
        using var tmp = TempDir.WithPathTo("app-state.json", out var path);
        await new AppStateStore(path).UpdateAsync(s => s with { Material = "soft_glass" });
        var state = await new AppStateStore(path).LoadAsync();
        await Assert.That(state.Material).IsEqualTo("soft_glass");
    }

    /// A value this build does not know must not cost the rest of the file: Read degrades any
    /// deserialization failure to defaults, which is why the field is a string and not an enum.
    [Test]
    public async Task An_unknown_material_leaves_every_other_field_intact() {
        using var tmp = TempDir.WithPathTo("app-state.json", out var path);
        File.WriteAllText(path, """{"shim_offered":true,"material":"frosted_titanium"}""");
        var state = await new AppStateStore(path).LoadAsync();
        await Assert.That(state.ShimOffered).IsTrue();
        await Assert.That(state.Material).IsEqualTo("frosted_titanium");
    }
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet build Capacitor.slnx`
Expected: FAIL, the `Capacitor.App.Materials` namespace does not exist.

- [ ] **Step 3: Write the model**

`src/Capacitor.App/Materials/SurfaceMaterial.cs`:

```csharp
namespace Capacitor.App.Materials;

public enum SurfaceMaterial { Opaque, SoftGlass, LiquidGlass }

public static class SurfaceMaterials {
    public static string ToStored(this SurfaceMaterial material) => material switch {
        SurfaceMaterial.SoftGlass => "soft_glass",
        SurfaceMaterial.LiquidGlass => "liquid_glass",
        _ => "opaque",
    };

    /// Null for anything unrecognised: a value written by a newer build reads as "no choice".
    public static SurfaceMaterial? Parse(string? stored) => stored switch {
        "opaque" => SurfaceMaterial.Opaque,
        "soft_glass" => SurfaceMaterial.SoftGlass,
        "liquid_glass" => SurfaceMaterial.LiquidGlass,
        _ => null,
    };

    public static bool IsGlass(this SurfaceMaterial material) => material != SurfaceMaterial.Opaque;
}
```

`src/Capacitor.App/Materials/MaterialAvailability.cs`:

```csharp
namespace Capacitor.App.Materials;

public enum MaterialAvailability { Available, NotCapable, PipelineFailed }
```

`src/Capacitor.App/Materials/MaterialState.cs`:

```csharp
namespace Capacitor.App.Materials;

/// FailureReason is set exactly when Availability is PipelineFailed.
public sealed record MaterialState(
    SurfaceMaterial Effective,
    SurfaceMaterial? Requested,
    MaterialAvailability Availability,
    string? FailureReason,
    bool ReduceTransparency) {
    public static MaterialState Opaque { get; } =
        new(SurfaceMaterial.Opaque, null, MaterialAvailability.Available, null, false);
}
```

`src/Capacitor.App/Materials/MaterialEnvironment.cs`:

```csharp
namespace Capacitor.App.Materials;

public sealed record MaterialEnvironment(bool GlassCapable, bool ReduceTransparency);
```

`src/Capacitor.App/Materials/IMaterialService.cs`:

```csharp
namespace Capacitor.App.Materials;

public interface IMaterialService {
    MaterialState Current { get; }

    /// Replays Current to a new subscriber, then every change of any field.
    IObservable<MaterialState> States { get; }

    Task SetAsync(SurfaceMaterial material);

    /// Latches for the session; later reports are ignored.
    void ReportPipelineFailure(string reason);
}
```

In `src/Capacitor.App/Services/AppStateStore.cs`, extend the record's parameter list:

```csharp
    IReadOnlyDictionary<string, string>? HarnessByRepo = null,
    // A SurfaceMaterials stored name. A string, not the enum: Read degrades any deserialization
    // failure to defaults, so an enum member this build does not know would reset the whole file.
    string? Material = null);
```

- [ ] **Step 4: Write the service**

`src/Capacitor.App/Materials/MaterialService.cs`:

```csharp
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Capacitor.App.Services;

namespace Capacitor.App.Materials;

public sealed class MaterialService : IMaterialService, IDisposable {
    readonly IAppStateStore _store;
    readonly MaterialEnvironment _environment;
    readonly BehaviorSubject<MaterialState> _states;
    readonly Lock _gate = new();
    SurfaceMaterial? _requested;
    string? _failure;

    public MaterialService(IAppStateStore store, MaterialEnvironment environment, SurfaceMaterial? requested) {
        _store = store;
        _environment = environment;
        _requested = requested;
        _states = new BehaviorSubject<MaterialState>(Resolve());
    }

    public static async Task<MaterialService> LoadAsync(IAppStateStore store, MaterialEnvironment environment) {
        var state = await store.LoadAsync().ConfigureAwait(false);
        return new MaterialService(store, environment, SurfaceMaterials.Parse(state.Material));
    }

    public MaterialState Current => _states.Value;

    public IObservable<MaterialState> States => _states.AsObservable();

    public async Task SetAsync(SurfaceMaterial material) {
        await _store.UpdateAsync(s => s with { Material = material.ToStored() }).ConfigureAwait(false);
        lock (_gate) _requested = material;
        Publish();
    }

    public void ReportPipelineFailure(string reason) {
        lock (_gate) {
            if (_failure is not null) return;
            _failure = reason;
        }
        Publish();
    }

    // SetAsync resumes off the UI thread while a failure report arrives on it, so the publish is
    // serialized with the mutation: an unsynchronized one could overwrite a latched failure.
    void Publish() {
        lock (_gate) {
            var next = Resolve();
            if (next != _states.Value) _states.OnNext(next);
        }
    }

    MaterialState Resolve() {
        var availability = !_environment.GlassCapable ? MaterialAvailability.NotCapable
            : _failure is not null ? MaterialAvailability.PipelineFailed
            : MaterialAvailability.Available;
        var effective = availability != MaterialAvailability.Available ? SurfaceMaterial.Opaque
            : _requested ?? (_environment.ReduceTransparency ? SurfaceMaterial.Opaque : SurfaceMaterial.SoftGlass);
        var reason = availability == MaterialAvailability.PipelineFailed ? _failure : null;
        return new MaterialState(effective, _requested, availability, reason, _environment.ReduceTransparency);
    }

    public void Dispose() => _states.Dispose();
}
```

- [ ] **Step 5: Run the tests**

Run: `dotnet build Capacitor.slnx` — expected 0 warnings, 0 errors.
Run the `MaterialServiceTests` and `AppStateStoreTests` classes with the treenode filter — expected all pass.

- [ ] **Step 6: Commit**

```bash
/usr/bin/git -C <worktree> add src/Capacitor.App/Materials src/Capacitor.App/Services/AppStateStore.cs test/Capacitor.App.Tests.Unit/InMemoryAppStateStore.cs test/Capacitor.App.Tests.Unit/MaterialServiceTests.cs test/Capacitor.App.Tests.Unit/AppStateStoreTests.cs
/usr/bin/git -C <worktree> commit -m "Resolve the surface material from choice, platform and failure" -m "The stored choice is a string because the state store resets the whole file on any deserialization failure." -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 3: Platform environment and the pipeline watch

**Files:**
- Create: `src/Capacitor.App/Views/AppKitAccessibility.cs`, `src/Capacitor.App/Materials/MaterialPipelineWatch.cs`
- Modify: `src/Capacitor.App/Materials/MaterialEnvironment.cs` (add `Detect`)
- Test: `test/Capacitor.App.Tests.Unit/MaterialPipelineWatchTests.cs`

**Interfaces:**
- Consumes: `IMaterialService.ReportPipelineFailure(string)`, `MaterialEnvironment`.
- Produces: `AppKitAccessibility.ReduceTransparency() : bool`; `MaterialEnvironment.Detect() : MaterialEnvironment`; `MaterialPipelineWatch(IMaterialService service, Action<Action<string>> subscribe, Action<Action<string>> unsubscribe, Action<Action> post) : IDisposable`.

- [ ] **Step 1: Write the failing test**

`test/Capacitor.App.Tests.Unit/MaterialPipelineWatchTests.cs`:

```csharp
using Capacitor.App.Materials;

namespace Capacitor.App.Tests.Unit;

public class MaterialPipelineWatchTests {
    static readonly MaterialEnvironment Mac = new(GlassCapable: true, ReduceTransparency: false);

    [Test]
    public async Task A_raised_event_becomes_one_report_posted_to_the_ui_thread() {
        var service = new MaterialService(new InMemoryAppStateStore(), Mac, SurfaceMaterial.SoftGlass);
        Action<string>? handler = null;
        var posted = new List<Action>();
        using var watch = new MaterialPipelineWatch(service, h => handler = h, _ => handler = null, posted.Add);

        handler!("shader did not compile");

        // Nothing reaches the service until the posted action runs: the event fires on the render thread.
        await Assert.That(service.Current.Availability).IsEqualTo(MaterialAvailability.Available);
        posted.Single()();
        await Assert.That(service.Current.FailureReason).IsEqualTo("shader did not compile");
    }

    [Test]
    public async Task Dispose_unsubscribes() {
        var service = new MaterialService(new InMemoryAppStateStore(), Mac, SurfaceMaterial.SoftGlass);
        Action<string>? handler = null;
        var watch = new MaterialPipelineWatch(service, h => handler = h, _ => handler = null, a => a());
        watch.Dispose();
        await Assert.That(handler).IsNull();
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet build Capacitor.slnx`
Expected: FAIL, `MaterialPipelineWatch` does not exist.

- [ ] **Step 3: Implement**

`src/Capacitor.App/Materials/MaterialPipelineWatch.cs`:

```csharp
namespace Capacitor.App.Materials;

/// The library raises its failure event on the render thread; `post` moves the report to the UI thread.
public sealed class MaterialPipelineWatch : IDisposable {
    readonly Action<string> _handler;
    readonly Action<Action<string>> _unsubscribe;

    public MaterialPipelineWatch(
            IMaterialService service, Action<Action<string>> subscribe,
            Action<Action<string>> unsubscribe, Action<Action> post) {
        _handler = reason => post(() => service.ReportPipelineFailure(reason));
        _unsubscribe = unsubscribe;
        subscribe(_handler);
    }

    public void Dispose() => _unsubscribe(_handler);
}
```

`src/Capacitor.App/Views/AppKitAccessibility.cs`:

```csharp
using System.Runtime.InteropServices;

namespace Capacitor.App.Views;

public static partial class AppKitAccessibility {
    const string ObjC = "/usr/lib/libobjc.A.dylib";

    public static bool ReduceTransparency() {
        if (!OperatingSystem.IsMacOS()) return false;

        var workspace = Send(GetClass("NSWorkspace"), Selector("sharedWorkspace"));
        return SendBool(workspace, Selector("accessibilityDisplayShouldReduceTransparency")) != 0;
    }

    [LibraryImport(ObjC, EntryPoint = "objc_getClass", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint GetClass(string name);

    [LibraryImport(ObjC, EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint Selector(string name);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial nint Send(nint receiver, nint selector);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial byte SendBool(nint receiver, nint selector);
}
```

Replace `src/Capacitor.App/Materials/MaterialEnvironment.cs` with:

```csharp
using Capacitor.App.Views;

namespace Capacitor.App.Materials;

public sealed record MaterialEnvironment(bool GlassCapable, bool ReduceTransparency) {
    /// Read once at startup. Glass is offered on macOS only: the one platform it was validated on.
    public static MaterialEnvironment Detect() =>
        new(OperatingSystem.IsMacOS(), AppKitAccessibility.ReduceTransparency());
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet build Capacitor.slnx` — expected 0 warnings, 0 errors.
Run the `MaterialPipelineWatchTests` class — expected 2 passed.

- [ ] **Step 5: Commit**

```bash
/usr/bin/git -C <worktree> add src/Capacitor.App/Materials src/Capacitor.App/Views/AppKitAccessibility.cs test/Capacitor.App.Tests.Unit/MaterialPipelineWatchTests.cs
/usr/bin/git -C <worktree> commit -m "Feed the material service its platform environment and pipeline failures" -m "The library raises its failure event on the render thread, so the report is posted to the UI thread rather than made in place." -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 4: MaterialScope and GlassLayer

**Files:**
- Create: `src/Capacitor.App/Controls/MaterialScope.cs`, `GlassKind.cs`, `GlassLayer.cs`, `GlassLayer.axaml`, `GlassStyles.axaml`
- Modify: `src/Capacitor.App/App.axaml` (one merged dictionary, one style include)
- Test: `test/Capacitor.App.Tests.Unit/GlassLayerTests.cs`

**Interfaces:**
- Consumes: `SurfaceMaterial`; `LiquidGlassSurface` from Task 1.
- Produces: `MaterialScope.MaterialProperty` (inherited `AttachedProperty<SurfaceMaterial>`, default `Opaque`), `MaterialScope.GetMaterial(Control)`, `MaterialScope.SetMaterial(Control, SurfaceMaterial)`; `enum GlassKind { Card, Rail, Panel, Chip }`; `GlassLayer : TemplatedControl` with `Kind`, template parts `PART_Glass` (`LiquidGlassSurface`) and `PART_Rim` (`Border`).

- [ ] **Step 1: Write the failing tests**

`test/Capacitor.App.Tests.Unit/GlassLayerTests.cs`:

```csharp
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.Controls;
using Capacitor.App.Materials;
using LiquidGlassAvaloniaUI;

namespace Capacitor.App.Tests.Unit;

[NotInParallel("AvaloniaSession")]
public class GlassLayerTests {
    static (Window Window, Panel Scope, GlassLayer Layer) Build(GlassKind kind, SurfaceMaterial material) {
        var layer = new GlassLayer { Kind = kind, CornerRadius = new CornerRadius(18), Width = 200, Height = 100 };
        var scope = new Panel { Children = { layer } };
        MaterialScope.SetMaterial(scope, material);
        var window = new Window { Content = scope, Width = 400, Height = 300 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, scope, layer);
    }

    static LiquidGlassSurface Glass(GlassLayer layer) => layer.GetVisualDescendants().OfType<LiquidGlassSurface>().Single();

    [Test]
    public Task The_scope_is_inherited_and_defaults_to_opaque() => AvaloniaSession.RunOnUiAsync(async () => {
        await Assert.That(MaterialScope.GetMaterial(new Button())).IsEqualTo(SurfaceMaterial.Opaque);
        var (window, _, layer) = Build(GlassKind.Card, SurfaceMaterial.LiquidGlass);
        try {
            await Assert.That(MaterialScope.GetMaterial(layer)).IsEqualTo(SurfaceMaterial.LiquidGlass);
        } finally { window.Close(); }
    });

    [Test]
    public Task A_card_takes_the_soft_then_the_liquid_parameters() => AvaloniaSession.RunOnUiAsync(async () => {
        var (window, scope, layer) = Build(GlassKind.Card, SurfaceMaterial.SoftGlass);
        try {
            var glass = Glass(layer);
            await Assert.That(glass.BlurRadius).IsEqualTo(14d);
            await Assert.That(glass.RefractionAmount).IsEqualTo(5d);
            await Assert.That(glass.ChromaticAberration).IsFalse();

            MaterialScope.SetMaterial(scope, SurfaceMaterial.LiquidGlass);
            Dispatcher.UIThread.RunJobs();
            await Assert.That(glass.BlurRadius).IsEqualTo(5d);
            await Assert.That(glass.RefractionAmount).IsEqualTo(32d);
            await Assert.That(glass.ChromaticAberration).IsTrue();
        } finally { window.Close(); }
    });

    [Test]
    public Task A_rail_takes_its_own_parameters() => AvaloniaSession.RunOnUiAsync(async () => {
        var (window, _, layer) = Build(GlassKind.Rail, SurfaceMaterial.SoftGlass);
        try {
            var glass = Glass(layer);
            await Assert.That(glass.BlurRadius).IsEqualTo(24d);
            await Assert.That(glass.HighlightFalloff).IsEqualTo(0.65);
        } finally { window.Close(); }
    });

    [Test]
    public Task The_host_radius_reaches_shader_and_rim_alike() => AvaloniaSession.RunOnUiAsync(async () => {
        var (window, _, layer) = Build(GlassKind.Card, SurfaceMaterial.SoftGlass);
        try {
            var rim = layer.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PART_Rim");
            await Assert.That(Glass(layer).CornerRadius).IsEqualTo(new CornerRadius(18));
            await Assert.That(rim.CornerRadius).IsEqualTo(new CornerRadius(18));
            await Assert.That(layer.IsHitTestVisible).IsFalse();
        } finally { window.Close(); }
    });
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet build Capacitor.slnx`
Expected: FAIL, `Capacitor.App.Controls` does not exist.

- [ ] **Step 3: Write the scope, the kind and the control**

`src/Capacitor.App/Controls/MaterialScope.cs`:

```csharp
using Avalonia;
using Avalonia.Controls;
using Capacitor.App.Materials;

namespace Capacitor.App.Controls;

/// Inherited, so a subtree pins its own material and a flyout takes its opener's.
public sealed class MaterialScope : AvaloniaObject {
    MaterialScope() { }

    public static readonly AttachedProperty<SurfaceMaterial> MaterialProperty =
        AvaloniaProperty.RegisterAttached<MaterialScope, Control, SurfaceMaterial>(
            "Material", SurfaceMaterial.Opaque, inherits: true);

    public static SurfaceMaterial GetMaterial(Control control) => control.GetValue(MaterialProperty);

    public static void SetMaterial(Control control, SurfaceMaterial value) => control.SetValue(MaterialProperty, value);
}
```

`src/Capacitor.App/Controls/GlassKind.cs`:

```csharp
namespace Capacitor.App.Controls;

public enum GlassKind { Card, Rail, Panel, Chip }
```

`src/Capacitor.App/Controls/GlassLayer.cs`:

```csharp
using Avalonia;
using Avalonia.Controls.Primitives;

namespace Capacitor.App.Controls;

/// The one place glass is drawn. Hosts place it behind their content and pass the radius and the
/// rim brush in; a Kind property rather than a class, because a template can bind a property.
public sealed class GlassLayer : TemplatedControl {
    public static readonly StyledProperty<GlassKind> KindProperty =
        AvaloniaProperty.Register<GlassLayer, GlassKind>(nameof(Kind));

    static GlassLayer() => IsHitTestVisibleProperty.OverrideDefaultValue<GlassLayer>(false);

    public GlassKind Kind {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }
}
```

`src/Capacitor.App/Controls/GlassLayer.axaml`:

```xml
<ResourceDictionary xmlns="https://github.com/avaloniaui"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:kcap="clr-namespace:Capacitor.App.Controls"
                    xmlns:glass="clr-namespace:LiquidGlassAvaloniaUI;assembly=LiquidGlassAvaloniaUI">
    <ControlTheme x:Key="{x:Type kcap:GlassLayer}" TargetType="kcap:GlassLayer">
        <Setter Property="BorderBrush" Value="Transparent" />
        <Setter Property="Template">
            <ControlTemplate>
                <Panel>
                    <glass:LiquidGlassSurface x:Name="PART_Glass" IsHitTestVisible="False"
                                             CornerRadius="{TemplateBinding CornerRadius}" />
                    <Border x:Name="PART_Rim" IsHitTestVisible="False" BorderThickness="1"
                            CornerRadius="{TemplateBinding CornerRadius}"
                            BorderBrush="{TemplateBinding BorderBrush}" />
                </Panel>
            </ControlTemplate>
        </Setter>
    </ControlTheme>
</ResourceDictionary>
```

- [ ] **Step 4: Write the parameter styles**

`src/Capacitor.App/Controls/GlassStyles.axaml`. The numbers are the prototype's; `Panel` starts from `Card`.

```xml
<Styles xmlns="https://github.com/avaloniaui"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:kcap="clr-namespace:Capacitor.App.Controls"
        xmlns:glass="clr-namespace:LiquidGlassAvaloniaUI;assembly=LiquidGlassAvaloniaUI">
    <Styles.Resources>
        <Color x:Key="KcapGlassCardTintSoft">#65151D29</Color>
        <Color x:Key="KcapGlassCardTintLiquid">#35151D29</Color>
        <Color x:Key="KcapGlassCardSurfaceSoft">#3812151D</Color>
        <Color x:Key="KcapGlassCardSurfaceLiquid">#1812151D</Color>
        <Color x:Key="KcapGlassCardShadow">#60000000</Color>
        <Color x:Key="KcapGlassRailTintSoft">#344E667C</Color>
        <Color x:Key="KcapGlassRailTintLiquid">#284E667C</Color>
        <Color x:Key="KcapGlassRailSurface">#462F3745</Color>
        <Color x:Key="KcapGlassRailShadow">#65000000</Color>
        <Color x:Key="KcapGlassChipTint">#16DCEFFF</Color>
        <Color x:Key="KcapGlassChipTintHover">#30DCEFFF</Color>
        <Color x:Key="KcapGlassChipSurface">#302C3F52</Color>
        <Color x:Key="KcapGlassChipSurfacePressed">#80172533</Color>
        <Color x:Key="KcapGlassChipShadow">#40040C16</Color>
        <SolidColorBrush x:Key="KcapGlassRimBrush" Color="#2EFFFFFF" />
        <SolidColorBrush x:Key="KcapGlassFocusBrush" Color="#B8E9F5" />
    </Styles.Resources>

    <!-- Card and Panel -->
    <Style Selector="kcap|GlassLayer[Kind=Card] /template/ glass|LiquidGlassSurface#PART_Glass,
                     kcap|GlassLayer[Kind=Panel] /template/ glass|LiquidGlassSurface#PART_Glass">
        <Setter Property="ShadowColor" Value="{StaticResource KcapGlassCardShadow}" />
        <Setter Property="ShadowRadius" Value="28" />
        <Setter Property="ShadowOffset" Value="0,12" />
    </Style>
    <Style Selector="kcap|GlassLayer[Kind=Card][(kcap|MaterialScope.Material)=SoftGlass] /template/ glass|LiquidGlassSurface#PART_Glass,
                     kcap|GlassLayer[Kind=Panel][(kcap|MaterialScope.Material)=SoftGlass] /template/ glass|LiquidGlassSurface#PART_Glass">
        <Setter Property="BlurRadius" Value="14" />
        <Setter Property="RefractionHeight" Value="12" />
        <Setter Property="RefractionAmount" Value="5" />
        <Setter Property="ChromaticAberration" Value="False" />
        <Setter Property="Vibrancy" Value="1.1" />
        <Setter Property="TintColor" Value="{StaticResource KcapGlassCardTintSoft}" />
        <Setter Property="SurfaceColor" Value="{StaticResource KcapGlassCardSurfaceSoft}" />
        <Setter Property="HighlightOpacity" Value="0.35" />
        <Setter Property="HighlightWidth" Value="0.7" />
    </Style>
    <Style Selector="kcap|GlassLayer[Kind=Card][(kcap|MaterialScope.Material)=LiquidGlass] /template/ glass|LiquidGlassSurface#PART_Glass,
                     kcap|GlassLayer[Kind=Panel][(kcap|MaterialScope.Material)=LiquidGlass] /template/ glass|LiquidGlassSurface#PART_Glass">
        <Setter Property="BlurRadius" Value="5" />
        <Setter Property="RefractionHeight" Value="22" />
        <Setter Property="RefractionAmount" Value="32" />
        <Setter Property="ChromaticAberration" Value="True" />
        <Setter Property="Vibrancy" Value="1.3" />
        <Setter Property="TintColor" Value="{StaticResource KcapGlassCardTintLiquid}" />
        <Setter Property="SurfaceColor" Value="{StaticResource KcapGlassCardSurfaceLiquid}" />
        <Setter Property="HighlightOpacity" Value="0.7" />
        <Setter Property="HighlightWidth" Value="1.1" />
    </Style>

    <!-- Rail -->
    <Style Selector="kcap|GlassLayer[Kind=Rail] /template/ glass|LiquidGlassSurface#PART_Glass">
        <Setter Property="RefractionHeight" Value="18" />
        <Setter Property="Vibrancy" Value="0.8" />
        <Setter Property="HighlightFalloff" Value="0.65" />
        <Setter Property="SurfaceColor" Value="{StaticResource KcapGlassRailSurface}" />
        <Setter Property="ShadowColor" Value="{StaticResource KcapGlassRailShadow}" />
        <Setter Property="ShadowRadius" Value="20" />
        <Setter Property="ShadowOffset" Value="4,8" />
    </Style>
    <Style Selector="kcap|GlassLayer[Kind=Rail][(kcap|MaterialScope.Material)=SoftGlass] /template/ glass|LiquidGlassSurface#PART_Glass">
        <Setter Property="BlurRadius" Value="24" />
        <Setter Property="RefractionAmount" Value="5" />
        <Setter Property="TintColor" Value="{StaticResource KcapGlassRailTintSoft}" />
        <Setter Property="HighlightOpacity" Value="0.28" />
        <Setter Property="HighlightWidth" Value="0.75" />
    </Style>
    <Style Selector="kcap|GlassLayer[Kind=Rail][(kcap|MaterialScope.Material)=LiquidGlass] /template/ glass|LiquidGlassSurface#PART_Glass">
        <Setter Property="BlurRadius" Value="18" />
        <Setter Property="RefractionAmount" Value="14" />
        <Setter Property="TintColor" Value="{StaticResource KcapGlassRailTintLiquid}" />
        <Setter Property="HighlightOpacity" Value="0.42" />
        <Setter Property="HighlightWidth" Value="1" />
    </Style>

    <!-- Chip -->
    <Style Selector="kcap|GlassLayer[Kind=Chip] /template/ glass|LiquidGlassSurface#PART_Glass">
        <Setter Property="RefractionHeight" Value="8" />
        <Setter Property="Vibrancy" Value="1.05" />
        <Setter Property="TintColor" Value="{StaticResource KcapGlassChipTint}" />
        <Setter Property="SurfaceColor" Value="{StaticResource KcapGlassChipSurface}" />
        <Setter Property="ShadowEnabled" Value="True" />
        <Setter Property="ShadowColor" Value="{StaticResource KcapGlassChipShadow}" />
        <Setter Property="ShadowRadius" Value="7" />
        <Setter Property="ShadowOffset" Value="0,2" />
    </Style>
    <Style Selector="kcap|GlassLayer[Kind=Chip][(kcap|MaterialScope.Material)=SoftGlass] /template/ glass|LiquidGlassSurface#PART_Glass">
        <Setter Property="BlurRadius" Value="8" />
        <Setter Property="RefractionAmount" Value="6" />
        <Setter Property="ChromaticAberration" Value="False" />
        <Setter Property="HighlightOpacity" Value="0.45" />
        <Setter Property="HighlightWidth" Value="0.65" />
    </Style>
    <Style Selector="kcap|GlassLayer[Kind=Chip][(kcap|MaterialScope.Material)=LiquidGlass] /template/ glass|LiquidGlassSurface#PART_Glass">
        <Setter Property="BlurRadius" Value="3" />
        <Setter Property="RefractionAmount" Value="12" />
        <Setter Property="ChromaticAberration" Value="True" />
        <Setter Property="HighlightOpacity" Value="0.7" />
        <Setter Property="HighlightWidth" Value="0.9" />
    </Style>
</Styles>
```

- [ ] **Step 5: Include both files from `App.axaml`**

In `Application.Resources`, inside the existing `ResourceDictionary.MergedDictionaries`, after the `PendingCardTemplates.axaml` include:

```xml
                <ResourceInclude Source="avares://Kurrent Capacitor/Controls/GlassLayer.axaml" />
```

As the LAST child of `Application.Styles`, after every inline style. Among styles of equal priority the later one wins, and the glass styles have to come after `App.axaml`'s own `Button.kcapChip`, `FlyoutPresenter.kcapPanel` and `MenuFlyoutPresenter.kcapPanel` styles. Every later task appends its include after this one, so the final order at the end of `Application.Styles` is `GlassStyles`, `SurfaceStyles`, `GlassChipStyles`, `GlassRailStyles`, `GlassFlyoutStyles`:

```xml
        <StyleInclude Source="avares://Kurrent Capacitor/Controls/GlassStyles.axaml" />
```

- [ ] **Step 6: Run the tests**

Run: `dotnet build Capacitor.slnx` — expected 0 warnings, 0 errors.
Run the `GlassLayerTests` class — expected 4 passed.

- [ ] **Step 7: Commit**

```bash
/usr/bin/git -C <worktree> add src/Capacitor.App/Controls src/Capacitor.App/App.axaml test/Capacitor.App.Tests.Unit/GlassLayerTests.cs
/usr/bin/git -C <worktree> commit -m "Draw glass through one layer keyed on an inherited material scope" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 5: Surface

**Files:**
- Create: `src/Capacitor.App/Controls/Surface.cs`, `Surface.axaml`, `SurfaceStyles.axaml`
- Modify: `src/Capacitor.App/App.axaml`
- Test: `test/Capacitor.App.Tests.Unit/SurfaceTests.cs`

**Interfaces:**
- Consumes: `MaterialScope`, `GlassLayer`, `GlassKind`, `LiquidGlassBackdrop.IsExcludedFromCapture`.
- Produces: `Surface : ContentControl` with `GlassCornerRadius : CornerRadius` (default 18) and `GlassKind : GlassKind` (default `Card`); classes `raised`, `rail`, `attachTarget`, `dragOver`; glass template parts `PART_GlassRoot` (`Panel`) and `PART_GlassLayer` (`GlassLayer`).

- [ ] **Step 1: Write the failing tests**

`test/Capacitor.App.Tests.Unit/SurfaceTests.cs`:

```csharp
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.Controls;
using Capacitor.App.Materials;
using LiquidGlassAvaloniaUI;

namespace Capacitor.App.Tests.Unit;

[NotInParallel("AvaloniaSession")]
public class SurfaceTests {
    static (Window Window, Panel Scope) Show(Surface surface, SurfaceMaterial material) {
        var scope = new Panel { Children = { surface } };
        MaterialScope.SetMaterial(scope, material);
        var window = new Window { Content = scope, Width = 400, Height = 300 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, scope);
    }

    static IBrush Resource(string key) => (IBrush)Application.Current!.FindResource(key)!;

    [Test]
    public Task Opaque_is_one_border_with_no_glass_in_it() => AvaloniaSession.RunOnUiAsync(async () => {
        var surface = new Surface { CornerRadius = new CornerRadius(12), Content = new TextBlock { Text = "x" } };
        var (window, _) = Show(surface, SurfaceMaterial.Opaque);
        try {
            await Assert.That(surface.GetVisualDescendants().OfType<LiquidGlassSurface>().Any()).IsFalse();
            var border = surface.GetVisualDescendants().OfType<Border>().First();
            await Assert.That(border.Background).IsEqualTo(Resource("KcapSurfaceBrush"));
            await Assert.That(border.BorderBrush).IsEqualTo(Resource("KcapBorderBrush"));
            await Assert.That(border.CornerRadius).IsEqualTo(new CornerRadius(12));
        } finally { window.Close(); }
    });

    [Test]
    public Task Raised_swaps_the_fill() => AvaloniaSession.RunOnUiAsync(async () => {
        var surface = new Surface { Classes = { "raised" } };
        var (window, _) = Show(surface, SurfaceMaterial.Opaque);
        try {
            await Assert.That(surface.Background).IsEqualTo(Resource("KcapSurfaceRaisedBrush"));
        } finally { window.Close(); }
    });

    [Test]
    public Task Glass_keeps_the_site_radius_for_opaque_only_and_excludes_itself_from_capture() => AvaloniaSession.RunOnUiAsync(async () => {
        var surface = new Surface { CornerRadius = new CornerRadius(12), Content = new TextBlock { Text = "x" } };
        var (window, _) = Show(surface, SurfaceMaterial.SoftGlass);
        try {
            var root = surface.GetVisualDescendants().OfType<Panel>().Single(p => p.Name == "PART_GlassRoot");
            var layer = surface.GetVisualDescendants().OfType<GlassLayer>().Single();
            var content = root.Children.OfType<Border>().Single();
            await Assert.That(LiquidGlassBackdrop.GetIsExcludedFromCapture(root)).IsTrue();
            await Assert.That(layer.CornerRadius).IsEqualTo(new CornerRadius(18));
            await Assert.That(content.CornerRadius).IsEqualTo(new CornerRadius(18));
            await Assert.That(layer.Kind).IsEqualTo(GlassKind.Card);
        } finally { window.Close(); }
    });

    [Test]
    public Task The_rail_class_selects_the_rail_kind_radius_and_rim() => AvaloniaSession.RunOnUiAsync(async () => {
        var surface = new Surface { Classes = { "rail" } };
        var (window, _) = Show(surface, SurfaceMaterial.LiquidGlass);
        try {
            var layer = surface.GetVisualDescendants().OfType<GlassLayer>().Single();
            await Assert.That(layer.Kind).IsEqualTo(GlassKind.Rail);
            await Assert.That(layer.CornerRadius).IsEqualTo(new CornerRadius(22));
            await Assert.That(layer.BorderBrush).IsEqualTo(Resource("KcapGlassRimBrush"));
        } finally { window.Close(); }
    });

    [Test]
    public Task A_pinned_subtree_stays_opaque_inside_a_glass_scope() => AvaloniaSession.RunOnUiAsync(async () => {
        var surface = new Surface();
        var pinned = new Panel { Children = { surface } };
        MaterialScope.SetMaterial(pinned, SurfaceMaterial.Opaque);
        var scope = new Panel { Children = { pinned } };
        MaterialScope.SetMaterial(scope, SurfaceMaterial.SoftGlass);
        var window = new Window { Content = scope };
        try {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            await Assert.That(surface.GetVisualDescendants().OfType<GlassLayer>().Any()).IsFalse();
        } finally { window.Close(); }
    });

    [Test]
    public Task A_material_switch_keeps_the_same_content_and_its_text() => AvaloniaSession.RunOnUiAsync(async () => {
        var box = new TextBox { Text = "ship it" };
        var surface = new Surface { Content = box };
        var (window, scope) = Show(surface, SurfaceMaterial.Opaque);
        try {
            MaterialScope.SetMaterial(scope, SurfaceMaterial.SoftGlass);
            Dispatcher.UIThread.RunJobs();
            await Assert.That(ReferenceEquals(surface.Content, box)).IsTrue();
            await Assert.That(box.Text).IsEqualTo("ship it");
            await Assert.That(box.IsAttachedToVisualTree()).IsTrue();
        } finally { window.Close(); }
    });

    [Test]
    [Arguments(SurfaceMaterial.Opaque)]
    [Arguments(SurfaceMaterial.SoftGlass)]
    public Task Drag_over_rings_the_card_in_the_primary_brush(SurfaceMaterial material) => AvaloniaSession.RunOnUiAsync(async () => {
        var surface = new Surface { Classes = { "attachTarget" } };
        var (window, _) = Show(surface, material);
        try {
            var resting = surface.BorderBrush;
            surface.Classes.Add("dragOver");
            Dispatcher.UIThread.RunJobs();
            await Assert.That(surface.BorderBrush).IsEqualTo(Resource("KcapPrimaryBrush"));
            await Assert.That(surface.BorderBrush).IsNotEqualTo(resting);
        } finally { window.Close(); }
    });
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet build Capacitor.slnx`
Expected: FAIL, `Surface` does not exist.

- [ ] **Step 3: Write the control**

`src/Capacitor.App/Controls/Surface.cs`:

```csharp
using Avalonia;
using Avalonia.Controls;

namespace Capacitor.App.Controls;

/// A card. CornerRadius is the site's and governs the opaque template only; the glass radius is a
/// material parameter, set by class styles and never at a site, where a local value would win.
public sealed class Surface : ContentControl {
    public static readonly StyledProperty<CornerRadius> GlassCornerRadiusProperty =
        AvaloniaProperty.Register<Surface, CornerRadius>(nameof(GlassCornerRadius), new CornerRadius(18));

    public static readonly StyledProperty<GlassKind> GlassKindProperty =
        AvaloniaProperty.Register<Surface, GlassKind>(nameof(GlassKind));

    public CornerRadius GlassCornerRadius {
        get => GetValue(GlassCornerRadiusProperty);
        set => SetValue(GlassCornerRadiusProperty, value);
    }

    public GlassKind GlassKind {
        get => GetValue(GlassKindProperty);
        set => SetValue(GlassKindProperty, value);
    }
}
```

`src/Capacitor.App/Controls/Surface.axaml` — the opaque theme. It holds no glass element, so the opaque look never touches the shader pipeline:

```xml
<ResourceDictionary xmlns="https://github.com/avaloniaui"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:kcap="clr-namespace:Capacitor.App.Controls">
    <ControlTheme x:Key="{x:Type kcap:Surface}" TargetType="kcap:Surface">
        <Setter Property="Background" Value="{StaticResource KcapSurfaceBrush}" />
        <Setter Property="BorderBrush" Value="{StaticResource KcapBorderBrush}" />
        <Setter Property="BorderThickness" Value="1" />
        <Setter Property="HorizontalContentAlignment" Value="Stretch" />
        <Setter Property="VerticalContentAlignment" Value="Stretch" />
        <Setter Property="Template">
            <ControlTemplate>
                <Border Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
                        BorderThickness="{TemplateBinding BorderThickness}"
                        CornerRadius="{TemplateBinding CornerRadius}" Padding="{TemplateBinding Padding}">
                    <ContentPresenter Name="PART_ContentPresenter" Content="{TemplateBinding Content}"
                                      ContentTemplate="{TemplateBinding ContentTemplate}"
                                      HorizontalContentAlignment="{TemplateBinding HorizontalContentAlignment}"
                                      VerticalContentAlignment="{TemplateBinding VerticalContentAlignment}" />
                </Border>
            </ControlTemplate>
        </Setter>
    </ControlTheme>
</ResourceDictionary>
```

`src/Capacitor.App/Controls/SurfaceStyles.axaml`. Order matters: among styles of equal priority the later one wins, so `dragOver` comes last.

```xml
<Styles xmlns="https://github.com/avaloniaui"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:kcap="clr-namespace:Capacitor.App.Controls"
        xmlns:glass="clr-namespace:LiquidGlassAvaloniaUI;assembly=LiquidGlassAvaloniaUI">
    <Style Selector="kcap|Surface.raised">
        <Setter Property="Background" Value="{StaticResource KcapSurfaceRaisedBrush}" />
    </Style>
    <Style Selector="kcap|Surface.rail">
        <Setter Property="GlassKind" Value="Rail" />
        <Setter Property="GlassCornerRadius" Value="22" />
    </Style>

    <!-- The root excludes the whole surface from the window snapshot: content drawn beside the
         glass would otherwise be blurred underneath itself. -->
    <Style Selector="kcap|Surface[(kcap|MaterialScope.Material)=SoftGlass],
                     kcap|Surface[(kcap|MaterialScope.Material)=LiquidGlass]">
        <Setter Property="BorderBrush" Value="Transparent" />
        <Setter Property="Template">
            <ControlTemplate>
                <Panel x:Name="PART_GlassRoot" glass:LiquidGlassBackdrop.IsExcludedFromCapture="True">
                    <kcap:GlassLayer x:Name="PART_GlassLayer" Kind="{TemplateBinding GlassKind}"
                                     CornerRadius="{TemplateBinding GlassCornerRadius}"
                                     BorderBrush="{TemplateBinding BorderBrush}" />
                    <Border CornerRadius="{TemplateBinding GlassCornerRadius}" Padding="{TemplateBinding Padding}"
                            ClipToBounds="True">
                        <ContentPresenter Name="PART_ContentPresenter" Content="{TemplateBinding Content}"
                                          ContentTemplate="{TemplateBinding ContentTemplate}"
                                          HorizontalContentAlignment="{TemplateBinding HorizontalContentAlignment}"
                                          VerticalContentAlignment="{TemplateBinding VerticalContentAlignment}" />
                    </Border>
                </Panel>
            </ControlTemplate>
        </Setter>
    </Style>
    <Style Selector="kcap|Surface.rail[(kcap|MaterialScope.Material)=SoftGlass],
                     kcap|Surface.rail[(kcap|MaterialScope.Material)=LiquidGlass]">
        <Setter Property="BorderBrush" Value="{StaticResource KcapGlassRimBrush}" />
    </Style>

    <Style Selector="kcap|Surface.attachTarget.dragOver">
        <Setter Property="BorderBrush" Value="{StaticResource KcapPrimaryBrush}" />
    </Style>
</Styles>
```

- [ ] **Step 4: Include both files from `App.axaml`**

Merged dictionary, after the `GlassLayer.axaml` include:

```xml
                <ResourceInclude Source="avares://Kurrent Capacitor/Controls/Surface.axaml" />
```

Style include, after the `GlassStyles.axaml` include:

```xml
        <StyleInclude Source="avares://Kurrent Capacitor/Controls/SurfaceStyles.axaml" />
```

- [ ] **Step 5: Run the tests**

Run: `dotnet build Capacitor.slnx` — expected 0 warnings, 0 errors.
Run the `SurfaceTests` class — expected 8 passed (the last test runs twice).

- [ ] **Step 6: Commit**

```bash
/usr/bin/git -C <worktree> add src/Capacitor.App/Controls src/Capacitor.App/App.axaml test/Capacitor.App.Tests.Unit/SurfaceTests.cs
/usr/bin/git -C <worktree> commit -m "Add the Surface control with an opaque and a glass template" -m "The glass template's root opts out of the window snapshot, or its own content is blurred underneath itself." -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 6: Migrate the cards to Surface

**Files:**
- Modify: the nine views below and `src/Capacitor.App/Views/WorkContextView.axaml`
- Modify tests: `ChatTabViewSmokeTests.cs` (one lookup), `RemoteSessionViewSmokeTests.cs`, `HomeViewSmokeTests.cs`, `WorkContextViewSmokeTests.cs`
- Untouched on purpose: `src/Capacitor.App/Views/ChatTabView.axaml` and the two `Border.attachTarget` styles in `App.axaml`

**Interfaces:**
- Consumes: `Surface` and its `raised`, `attachTarget`, `dragOver` classes.
- Produces: `GoalCard`, `QuestionCard`, `AcpQuestionCard`, `AccessBanner` and `StartingPanel` are `Surface`s with the same names. `ComposerCard`, `QueuedMessagesBanner` and the `systemNote` and `toolGroup` rows stay `Border`s.

**The chat view stays as it is.** Its four cards are not migrated: two are rows of the virtualised list, where a templated control costs three visuals for every one, and the view always sits in an opaque scope. The permission request and the two elicitation questions in `PendingCardTemplates.axaml` do migrate, by the user's decision, even though the chat hosts them.

The rule, for every site below:

```xml
<!-- before -->
<Border x:Name="N" Classes="c" Background="{StaticResource KcapSurfaceBrush}"
        BorderBrush="{StaticResource KcapBorderBrush}" BorderThickness="1"
        CornerRadius="R" Padding="P" OtherAttribute="…">
    <Child />
</Border>

<!-- after -->
<kcap:Surface x:Name="N" Classes="c" CornerRadius="R" Padding="P" OtherAttribute="…">
    <Child />
</kcap:Surface>
```

Line numbers below are as of the branch point; locate each site by its name, class or content once earlier edits have shifted them.

Drop `Background`, `BorderBrush` and `BorderThickness`: the theme supplies them. Where the brush was `KcapSurfaceRaisedBrush`, add the class `raised`. Keep every other attribute, including `Classes.<name>="{Binding …}"`. Add `xmlns:kcap="clr-namespace:Capacitor.App.Controls"` to the file's root element. Never set `BorderBrush` on a `Surface` at a site.

| # | File:line | Name / class | Raised |
|---|---|---|---|
| 1 | `Views/HomeView.axaml:50` | — | |
| 2 | `Views/LauncherPaneView.axaml:31` | `GoalCard`, class `attachTarget` | |
| 3–9 | `Views/Onboarding/OnboardingWindow.axaml:52, 110, 163, 210, 230, 273, 301` | — | |
| 10–11 | `Views/Onboarding/SignInStepView.axaml:47, 102` | — | |
| 12 | `Views/PendingCardTemplates.axaml:7` | permission request | yes |
| 13 | `Views/PendingCardTemplates.axaml:36` | `QuestionCard` | yes |
| 14 | `Views/PendingCardTemplates.axaml:229` | `AcpQuestionCard` | yes |
| 15 | `Views/PullRequestCard.axaml:7` | — | yes |
| 16 | `Views/RemoteSessionView.axaml:74` | `AccessBanner` | |
| 17 | `Views/RemoteSessionView.axaml:95` | — | |
| 18–19 | `Views/SettingsWindow.axaml:37, 57` | — | |
| 20–25 | `Views/WorkspaceView.axaml:101, 121, 133, 140, 149, 167` | `StartingPanel` on the last | |
| 26 | `Views/WorkContextView.axaml:135` | class `card` (class-styled) | yes |

Not migrated, and why: `ChatTabView.axaml:55, 96, 168, 194` are the chat view's own cards (see above); `AttachmentChipStrip.axaml:13` is a chip; `SettingsWindow.axaml:15` is a pill; `OnboardingWindow.axaml:315` is a self-closing ring; `SessionRailView.axaml:52` is the rail root (Task 8); `SessionRailView.axaml:245`, `ChatTabView.axaml:42`, `AttachmentChipStrip.axaml:19`, `PullRequestReader.axaml:88` and `:150` have no border; `WorkContextView.axaml:173` overrides its card class to transparent.

- [ ] **Step 1: Point the five test lookups at `Surface` first**

These lookups fail to find their element until the views change, which is the failing test for this task. Add `using Capacitor.App.Controls;` to each file.

- `ChatTabViewSmokeTests.cs:1097` — `OfType<Border>().Single(b => b.Name == "QuestionCard")` → `OfType<Surface>().Single(…)`. This is the only change in that file: the lookups at `:442` (`QueuedMessagesBanner`), `:944` (`systemNote`), `:1038` (`toolGroup`) and `:1308` (`ComposerCard`) stay `Border`, because those cards do.
- `RemoteSessionViewSmokeTests.cs:87` — `OfType<Border>().Any(b => b.Name == "AcpQuestionCard")` → `OfType<Surface>().Any(…)`
- `RemoteSessionViewSmokeTests.cs:106` — `FindControl<Border>("AccessBanner")` → `FindControl<Surface>("AccessBanner")`
- `HomeViewSmokeTests.cs:695` — `Find<Border>(window, "GoalCard")` → `Find<Surface>(window, "GoalCard")`
- `WorkContextViewSmokeTests.cs:118` — `OfType<Border>().First(b => b.Classes.Contains("card"))` → `OfType<Surface>().First(…)`

Every assertion after these lookups stays as written: `Surface` is a `TemplatedControl` and has `BorderBrush`, `Classes` and `Name`.

- [ ] **Step 2: Run to verify failure**

Run the `HomeViewSmokeTests` class.
Expected: `A_drop_on_the_goal_card_stages_the_file` FAILS, no `Surface` named `GoalCard`.

- [ ] **Step 3: Migrate the twenty-six sites**

Apply the rule to each row. For `WorkContextView.axaml`, the card's look comes from a `Border.card` style in the view's own `Styles` (lines 115–121): change `<Border Classes="card" …>` at line 135 to `<kcap:Surface Classes="card raised" …>`, retarget every `Border.card` selector in that file to `kcap|Surface.card`, and in the base style delete the `Background`, `BorderBrush` and `BorderThickness` setters (the theme and `raised` supply them) while keeping `CornerRadius` and `Padding`. The hover style that sets `BorderBrush` to `KcapFaintBrush` keeps working: a style outranks the theme. Line 173 stays a `Border`; its class no longer matches anything and its local values already made it invisible.

- [ ] **Step 4: Retarget the selectors that named a migrated card**

Run, from the repo root:

```bash
rtk proxy grep -rnE --include='*.axaml' 'Border\.card|Border#(GoalCard|QuestionCard|AcpQuestionCard|AccessBanner|StartingPanel)' src/Capacitor.App
```

Change each hit from `Border…` to `kcap|Surface…` (add the `kcap` namespace to that file if missing). Leave the `Border.attachTarget` and `Border.attachTarget.dragOver` styles in `App.axaml` exactly as they are: the chat's `ComposerCard` is still a `Border` and still needs them. `GoalCard` is served by `kcap|Surface.attachTarget.dragOver` in `SurfaceStyles.axaml`, and its resting brush now comes from the `Surface` theme.

Confirm the chat view is untouched: `/usr/bin/git -C <worktree> diff --stat -- src/Capacitor.App/Views/ChatTabView.axaml` must print nothing.

- [ ] **Step 5: Build, then run every app UI suite**

Run: `dotnet build Capacitor.slnx` — expected 0 warnings, 0 errors. An `AVLN` XAML warning here is an error in CI.
Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj`
Expected: every test passes. The permission and question cards are rows of the chat list, so a chat layout failure means one of them changed its first-measure size; check that the `Surface` kept the `Border`'s `Margin`, alignment and `Padding` exactly.

- [ ] **Step 6: Commit**

```bash
/usr/bin/git -C <worktree> add src/Capacitor.App test/Capacitor.App.Tests.Unit
/usr/bin/git -C <worktree> commit -m "Define cards outside the chat view through the Surface control" -m "The chat's own cards stay Borders: two are rows of the virtualised list, where a templated control triples the visuals." -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 7: Launcher chips under glass

**Files:**
- Create: `src/Capacitor.App/Controls/GlassChipStyles.axaml`
- Modify: `src/Capacitor.App/Views/LauncherPaneView.axaml` (five chips), `src/Capacitor.App/App.axaml`
- Test: `test/Capacitor.App.Tests.Unit/GlassChipTests.cs`

**Interfaces:**
- Consumes: `GlassLayer` (`Kind="Chip"`), `MaterialScope`, the colour resources from `GlassStyles.axaml`.
- Produces: class `picker` on `RepositoryChip`, `MachineChip`, `AgentChip`, `EffortChip`, `PermissionChip`; glass template parts `ChipGlass`, `ChipContent`, `FocusRing`.

- [ ] **Step 1: Write the failing tests**

`test/Capacitor.App.Tests.Unit/GlassChipTests.cs`:

```csharp
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.Controls;
using Capacitor.App.Materials;
using LiquidGlassAvaloniaUI;

namespace Capacitor.App.Tests.Unit;

[NotInParallel("AvaloniaSession")]
public class GlassChipTests {
    static (Window Window, Panel Scope) Show(Button chip, SurfaceMaterial material) {
        var scope = new Panel { Children = { chip } };
        MaterialScope.SetMaterial(scope, material);
        var window = new Window { Content = scope, Width = 400, Height = 200 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, scope);
    }

    static Button Picker() => new() { Classes = { "kcapChip", "picker" }, Content = "repo" };

    [Test]
    public Task An_opaque_picker_keeps_the_pill_and_its_fill() => AvaloniaSession.RunOnUiAsync(async () => {
        var chip = Picker();
        var (window, _) = Show(chip, SurfaceMaterial.Opaque);
        try {
            var presenter = chip.GetVisualDescendants().OfType<ContentPresenter>().Single(p => p.Name == "PART_ContentPresenter");
            await Assert.That(chip.Padding).IsEqualTo(new Thickness(11, 5));
            await Assert.That(chip.CornerRadius).IsEqualTo(new CornerRadius(999));
            await Assert.That(presenter.Background).IsNotNull();
            await Assert.That(chip.GetVisualDescendants().OfType<GlassLayer>().Any()).IsFalse();
        } finally { window.Close(); }
    });

    [Test]
    public Task A_glass_picker_has_a_chip_layer_and_a_presenter_no_theme_style_can_fill() => AvaloniaSession.RunOnUiAsync(async () => {
        var chip = Picker();
        var (window, _) = Show(chip, SurfaceMaterial.SoftGlass);
        try {
            var layer = chip.GetVisualDescendants().OfType<GlassLayer>().Single();
            // The vendored glass surface has a presenter of its own, named PART_ContentPresenter, deep
            // inside the layer: look the chip's up by name, never by type alone.
            var presenters = chip.GetVisualDescendants().OfType<ContentPresenter>().ToList();
            var presenter = presenters.Single(p => p.Name == "ChipContent");
            await Assert.That(layer.Kind).IsEqualTo(GlassKind.Chip);
            await Assert.That(layer.CornerRadius).IsEqualTo(new CornerRadius(12));
            // What Fluent's per-state styles target is a PART_ContentPresenter in the BUTTON's own template.
            await Assert.That(presenters.Any(p => p.Name == "PART_ContentPresenter" && ReferenceEquals(p.TemplatedParent, chip))).IsFalse();
            await Assert.That(chip.Padding).IsEqualTo(new Thickness(12, 7));

            foreach (var state in new[] { ":pointerover", ":pressed", ":disabled" }) {
                ((IPseudoClasses)chip.Classes).Set(state, true);
                Dispatcher.UIThread.RunJobs();
                await Assert.That(presenter.Background).IsNull();
                await Assert.That(presenter.BorderBrush).IsNull();
                ((IPseudoClasses)chip.Classes).Set(state, false);
            }
        } finally { window.Close(); }
    });

    [Test]
    public Task A_chip_that_is_not_a_picker_stays_opaque_under_glass() => AvaloniaSession.RunOnUiAsync(async () => {
        var chip = new Button { Classes = { "kcapChip" }, Content = "Activity" };
        var (window, _) = Show(chip, SurfaceMaterial.LiquidGlass);
        try {
            await Assert.That(chip.GetVisualDescendants().OfType<GlassLayer>().Any()).IsFalse();
        } finally { window.Close(); }
    });

    /// The any-glass style is two selector arms around one template: without this, a dropped
    /// LiquidGlass arm would leave Liquid glass opaque and no test would notice.
    [Test]
    public Task A_liquid_glass_picker_takes_the_glass_template_too() => AvaloniaSession.RunOnUiAsync(async () => {
        var chip = Picker();
        var (window, _) = Show(chip, SurfaceMaterial.LiquidGlass);
        try {
            await Assert.That(chip.GetVisualDescendants().OfType<GlassLayer>().Single().Kind).IsEqualTo(GlassKind.Chip);
            await Assert.That(chip.Padding).IsEqualTo(new Thickness(12, 7));
            await Assert.That(chip.GetVisualDescendants().OfType<ContentPresenter>().Any(p => p.Name == "ChipContent")).IsTrue();
            await Assert.That(Glass(chip).ChromaticAberration).IsTrue();
        } finally { window.Close(); }
    });

    [Test]
    public Task Each_state_moves_the_glass_it_is_meant_to_move() => AvaloniaSession.RunOnUiAsync(async () => {
        var chip = Picker();
        var (window, _) = Show(chip, SurfaceMaterial.SoftGlass);
        try {
            var glass = Glass(chip);
            var ring = chip.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "FocusRing");
            var root = chip.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "ChipRoot");
            var resting = (glass.TintColor, glass.SurfaceColor, glass.HighlightOpacity, glass.ShadowEnabled);
            await Assert.That(resting.HighlightOpacity).IsEqualTo(0.45);
            await Assert.That(resting.ShadowEnabled).IsTrue();
            await Assert.That(ring.IsVisible).IsFalse();
            await Assert.That(root.Opacity).IsEqualTo(1d);

            Set(chip, ":pointerover", true);
            await Assert.That(glass.TintColor).IsEqualTo(Color.Parse("#30DCEFFF"));
            await Assert.That(glass.HighlightOpacity).IsEqualTo(0.85);
            Set(chip, ":pointerover", false);

            Set(chip, ":pressed", true);
            await Assert.That(glass.SurfaceColor).IsEqualTo(Color.Parse("#80172533"));
            await Assert.That(glass.HighlightOpacity).IsEqualTo(0.4);
            await Assert.That(glass.ShadowEnabled).IsFalse();
            Set(chip, ":pressed", false);

            Set(chip, ":focus-visible", true);
            await Assert.That(ring.IsVisible).IsTrue();
            Set(chip, ":focus-visible", false);

            Set(chip, ":disabled", true);
            await Assert.That(root.Opacity).IsEqualTo(0.45);
            Set(chip, ":disabled", false);

            await Assert.That((glass.TintColor, glass.SurfaceColor, glass.HighlightOpacity, glass.ShadowEnabled)).IsEqualTo(resting);
        } finally { window.Close(); }
    });

    static LiquidGlassSurface Glass(Button chip) => chip.GetVisualDescendants().OfType<LiquidGlassSurface>().Single();

    static void Set(Button chip, string state, bool on) {
        ((IPseudoClasses)chip.Classes).Set(state, on);
        Dispatcher.UIThread.RunJobs();
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run the `GlassChipTests` class.
Expected: FAIL. The opaque test sees padding `14,8`, and the glass test finds no `GlassLayer`.

- [ ] **Step 3: Write the chip styles**

`src/Capacitor.App/Controls/GlassChipStyles.axaml`. The presenter is deliberately not named `PART_ContentPresenter`: Fluent's per-state `Button` styles and the app's four `Button.kcapChip` presenter styles all target that name, and they survive a `Template` swap.

```xml
<Styles xmlns="https://github.com/avaloniaui"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:kcap="clr-namespace:Capacitor.App.Controls"
        xmlns:glass="clr-namespace:LiquidGlassAvaloniaUI;assembly=LiquidGlassAvaloniaUI">
    <!-- Local Padding and CornerRadius on the chips would outrank these, so they live here. -->
    <Style Selector="Button.kcapChip.picker">
        <Setter Property="Padding" Value="11,5" />
        <Setter Property="CornerRadius" Value="999" />
    </Style>

    <Style Selector="Button.kcapChip.picker[(kcap|MaterialScope.Material)=SoftGlass],
                     Button.kcapChip.picker[(kcap|MaterialScope.Material)=LiquidGlass]">
        <Setter Property="Padding" Value="12,7" />
        <Setter Property="FocusAdorner" Value="{x:Null}" />
        <Setter Property="Template">
            <ControlTemplate>
                <Grid x:Name="ChipRoot" Background="Transparent" glass:LiquidGlassBackdrop.IsExcludedFromCapture="True">
                    <kcap:GlassLayer x:Name="ChipGlass" Kind="Chip" CornerRadius="12" />
                    <Grid ColumnDefinitions="*,Auto" ColumnSpacing="8" Margin="{TemplateBinding Padding}">
                        <ContentPresenter Name="ChipContent" Content="{TemplateBinding Content}"
                                          ContentTemplate="{TemplateBinding ContentTemplate}"
                                          Foreground="{TemplateBinding Foreground}" VerticalAlignment="Center"
                                          HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}" />
                        <Path Grid.Column="1" Data="M1,2 L4,5 L7,2" Width="8" Height="7"
                              Stroke="{TemplateBinding Foreground}" StrokeThickness="1.3" StrokeLineCap="Round"
                              StrokeJoin="Round" Opacity="0.6" VerticalAlignment="Center" IsHitTestVisible="False" />
                    </Grid>
                    <Border x:Name="FocusRing" CornerRadius="12" BorderThickness="2" IsVisible="False"
                            BorderBrush="{StaticResource KcapGlassFocusBrush}" IsHitTestVisible="False" />
                </Grid>
            </ControlTemplate>
        </Setter>
    </Style>

    <Style Selector="Button.kcapChip.picker:pointerover /template/ kcap|GlassLayer#ChipGlass /template/ glass|LiquidGlassSurface#PART_Glass">
        <Setter Property="TintColor" Value="{StaticResource KcapGlassChipTintHover}" />
        <Setter Property="HighlightOpacity" Value="0.85" />
    </Style>
    <Style Selector="Button.kcapChip.picker:pressed /template/ kcap|GlassLayer#ChipGlass /template/ glass|LiquidGlassSurface#PART_Glass">
        <Setter Property="SurfaceColor" Value="{StaticResource KcapGlassChipSurfacePressed}" />
        <Setter Property="HighlightOpacity" Value="0.4" />
        <Setter Property="ShadowEnabled" Value="False" />
    </Style>
    <Style Selector="Button.kcapChip.picker:focus-visible /template/ Border#FocusRing">
        <Setter Property="IsVisible" Value="True" />
    </Style>
    <Style Selector="Button.kcapChip.picker:disabled /template/ Grid#ChipRoot">
        <Setter Property="Opacity" Value="0.45" />
    </Style>
</Styles>
```

Include it from `App.axaml`, after the `SurfaceStyles.axaml` include, so its state styles are declared after the layer's own parameters:

```xml
        <StyleInclude Source="avares://Kurrent Capacitor/Controls/GlassChipStyles.axaml" />
```

- [ ] **Step 4: Mark the five launcher chips**

In `src/Capacitor.App/Views/LauncherPaneView.axaml`, on `RepositoryChip`, `MachineChip`, `AgentChip`, `EffortChip` and `PermissionChip`: change `Classes="kcapChip"` to `Classes="kcapChip picker"` and delete the local `CornerRadius="999"` and `Padding="11,5"` attributes. Leave `Foreground` and everything else.

- [ ] **Step 5: Run the tests**

Run: `dotnet build Capacitor.slnx` — expected 0 warnings, 0 errors.
Run the `GlassChipTests` and `HomeViewSmokeTests` classes — expected all pass.

- [ ] **Step 6: Commit**

```bash
/usr/bin/git -C <worktree> add src/Capacitor.App/Controls/GlassChipStyles.axaml src/Capacitor.App/App.axaml src/Capacitor.App/Views/LauncherPaneView.axaml test/Capacitor.App.Tests.Unit/GlassChipTests.cs
/usr/bin/git -C <worktree> commit -m "Give the launcher chips a glass template" -m "The presenter is renamed because Fluent's per-state fills target PART_ContentPresenter and survive a Template swap." -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 8: Rail, backdrop and the window's scope

**Files:**
- Create: `src/Capacitor.App/Controls/MaterialBackdrop.cs`, `src/Capacitor.App/Controls/GlassRailStyles.axaml`
- Modify: `src/Capacitor.App/Views/MainWindow.axaml`, `src/Capacitor.App/Views/SessionRailView.axaml`, `src/Capacitor.App/ViewModels/MainWindowViewModel.cs`, `src/Capacitor.App/App.axaml`, `src/Capacitor.App/App.axaml.cs`
- Test: `test/Capacitor.App.Tests.Unit/MaterialWindowTests.cs`

**Interfaces:**
- Consumes: `IMaterialService`, `MaterialState`, `MaterialEnvironment.Detect()`, `MaterialPipelineWatch`, `Surface` (`rail`), `MaterialScope`, `LiquidGlassPipeline.Unavailable`.
- Produces: `MainWindowViewModel(…, IObservable<MaterialState>? material = null)` with `Material : SurfaceMaterial` and `IsGlass : bool`; `App.BuildAndShowMainWindow(…, IObservable<MaterialState>? material = null)`; an `App` field `_material : MaterialService?`; controls named `MaterialBackdrop`, `RailSurface`, `RailChrome`.

- [ ] **Step 1: Write the failing tests**

`test/Capacitor.App.Tests.Unit/MaterialWindowTests.cs`:

```csharp
using System.Reactive.Subjects;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.Controls;
using Capacitor.App.Materials;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.App.Views;
using Capacitor.Cli.Core.LocalIpc;
using DynamicData;
using static Capacitor.App.Tests.Unit.FakeDaemonClientService;

namespace Capacitor.App.Tests.Unit;

[NotInParallel("AvaloniaSession")]
public class MaterialWindowTests {
    static MaterialState State(SurfaceMaterial material) => MaterialState.Opaque with { Effective = material };

    static (MainWindow Window, BehaviorSubject<MaterialState> Material) Build(SurfaceMaterial material) {
        var states = new BehaviorSubject<MaterialState>(State(material));
        var vm = new MainWindowViewModel(new FakeDaemonClientService(), CancellationToken.None, TestActivity.New(),
            TimeProvider.System, material: states);
        var window = new MainWindow { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, states);
    }

    [Test]
    public Task Without_a_material_source_the_window_is_opaque() => AvaloniaSession.RunOnUiAsync(async () => {
        var vm = new MainWindowViewModel(new FakeDaemonClientService(), CancellationToken.None, TestActivity.New(), TimeProvider.System);
        var window = new MainWindow { DataContext = vm };
        try {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            await Assert.That(vm.Material).IsEqualTo(SurfaceMaterial.Opaque);
            await Assert.That(window.FindControl<MaterialBackdrop>("MaterialBackdrop")!.IsVisible).IsFalse();
        } finally { window.Close(); }
    });

    [Test]
    public Task The_scope_follows_the_service_and_the_workspace_host_stays_opaque() => AvaloniaSession.RunOnUiAsync(async () => {
        var (window, material) = Build(SurfaceMaterial.SoftGlass);
        try {
            var sessions = window.FindControl<Grid>("SessionsSurface")!;
            var host = window.FindControl<ContentControl>("WorkspaceHost")!;
            await Assert.That(MaterialScope.GetMaterial(sessions)).IsEqualTo(SurfaceMaterial.SoftGlass);
            await Assert.That(MaterialScope.GetMaterial(host)).IsEqualTo(SurfaceMaterial.Opaque);
            await Assert.That(host.Background).IsNotNull();
            await Assert.That(window.FindControl<MaterialBackdrop>("MaterialBackdrop")!.IsVisible).IsTrue();

            material.OnNext(State(SurfaceMaterial.Opaque));
            Dispatcher.UIThread.RunJobs();
            await Assert.That(MaterialScope.GetMaterial(sessions)).IsEqualTo(SurfaceMaterial.Opaque);
            await Assert.That(window.FindControl<MaterialBackdrop>("MaterialBackdrop")!.IsVisible).IsFalse();
        } finally { window.Close(); }
    });

    [Test]
    public Task The_rail_docks_when_opaque_and_floats_under_glass() => AvaloniaSession.RunOnUiAsync(async () => {
        var (window, material) = Build(SurfaceMaterial.Opaque);
        try {
            var rail = window.FindControl<SessionRailView>("SessionRail")!;
            var chrome = rail.FindControl<Grid>("RailChrome")!;
            await Assert.That(rail.Width).IsEqualTo(310d);
            await Assert.That(rail.Margin).IsEqualTo(new Thickness(0));
            await Assert.That(chrome.Height).IsEqualTo(44d);

            material.OnNext(State(SurfaceMaterial.LiquidGlass));
            Dispatcher.UIThread.RunJobs();
            await Assert.That(rail.Width).IsEqualTo(310d);
            await Assert.That(rail.Margin).IsEqualTo(new Thickness(12, 40, 12, 12));
            await Assert.That(chrome.Height).IsEqualTo(16d);
            await Assert.That(rail.FindControl<Surface>("RailSurface")!.GlassKind).IsEqualTo(GlassKind.Rail);

            // The glass button fill applies through the presenter although the Button sets its own
            // Background locally; the row fills are pinned by their own test.
            var presenter = rail.FindControl<Button>("RailNewSessionButton")!.GetVisualDescendants()
                .OfType<ContentPresenter>().First(p => p.Name == "PART_ContentPresenter");
            await Assert.That(presenter.Background).IsEqualTo((IBrush)Application.Current!.FindResource("KcapGlassRailButtonBrush")!);
        } finally { window.Close(); }
    });

    static (MainWindow Window, Button Row) RailRowWindow(SurfaceMaterial material) {
        var service = new FakeDaemonClientService();
        service.SnapshotsSubject.OnNext(Snap());
        service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
        service.Agents.AddOrUpdate(new AgentStatusDto(
            "a1", "agent", "claude", "/dev/alpha/wt/feature-x", "Running",
            null, null, null, DateTime.UtcNow, null, null, Title: "Fix the flaky test"));

        Func<string, string> resolveRepoRoot = p => p.Contains("/wt/", StringComparison.Ordinal)
            ? p[..p.IndexOf("/wt/", StringComparison.Ordinal)]
            : p;
        var directory = new AgentDirectory(
            service, new FakeRemoteAgents(), new FakeServerLane(), new RepoIdentityResolver(_ => null),
            resolveRepoRoot, null, null, TimeProvider.System);
        var rail = new SessionRailViewModel(directory, _ => { }, _ => { }, TimeProvider.System, resolveRepoRoot);
        var vm = new MainWindowViewModel(service, CancellationToken.None, TestActivity.New(), TimeProvider.System,
            rail: rail, material: new BehaviorSubject<MaterialState>(State(material)));
        var window = new MainWindow { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // The selection the rail paints, without opening a workspace: SwapTo sets exactly this.
        rail.SelectedAgentId = "a1";
        Dispatcher.UIThread.RunJobs();

        var row = window.GetVisualDescendants().OfType<Button>()
            .First(b => b.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "Fix the flaky test"));
        return (window, row);
    }

    /// The glass row fill and radius have to be declared where they outrank the rail's own opaque
    /// row styles; declared at application level they lose the tie and this test sees the opaque brush.
    [Test]
    public Task A_selected_rail_row_takes_the_glass_fill_under_glass() => AvaloniaSession.RunOnUiAsync(async () => {
        var (window, row) = RailRowWindow(SurfaceMaterial.SoftGlass);
        try {
            await Assert.That(row.Classes.Contains("selected")).IsTrue();
            await Assert.That(row.Background).IsEqualTo((IBrush)Application.Current!.FindResource("KcapGlassRailRowBrush")!);
            await Assert.That(row.CornerRadius).IsEqualTo(new CornerRadius(9));
        } finally { window.Close(); }
    });
}
```

The rail keeps its 310 width in both materials; the margin widens its `Auto` column from 310 to 334, which is the prototype's layout.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet build Capacitor.slnx`
Expected: FAIL, `MainWindowViewModel` has no `material` parameter and `MaterialBackdrop` does not exist.

- [ ] **Step 3: Write the backdrop**

`src/Capacitor.App/Controls/MaterialBackdrop.cs`:

```csharp
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Capacitor.App.Controls;

/// Glass over the flat canvas refracts nothing, so the shell paints something behind both panes.
/// Static on purpose: following the pointer re-captures the window and redraws every glass surface.
public sealed class MaterialBackdrop : Control {
    public static readonly StyledProperty<double> RailWidthProperty =
        AvaloniaProperty.Register<MaterialBackdrop, double>(nameof(RailWidth), 334);

    static readonly Color RailTeal = Color.Parse("#553D7581");
    static readonly Color RailViolet = Color.Parse("#344D4878");
    static readonly Color PaneGreen = Color.Parse("#8023806C");
    static readonly Color PaneViolet = Color.Parse("#6851528F");
    static readonly Color PaneBlue = Color.Parse("#45226B85");

    static MaterialBackdrop() {
        AffectsRender<MaterialBackdrop>(RailWidthProperty);
        IsHitTestVisibleProperty.OverrideDefaultValue<MaterialBackdrop>(false);
    }

    public double RailWidth {
        get => GetValue(RailWidthProperty);
        set => SetValue(RailWidthProperty, value);
    }

    public override void Render(DrawingContext context) {
        var l = RailWidth;
        var w = Bounds.Width - l;
        var h = Bounds.Height;
        if (w <= 0 || h <= 0) return;

        Glow(context, RailTeal, new Point(l * 0.35, h * 0.42), l * 1.25, h * 0.7);
        Glow(context, RailViolet, new Point(l * 0.5, h * 0.85), l, h * 0.45);
        Glow(context, PaneGreen, new Point(l + w * 0.3, h * 0.55), w * 0.43, h * 0.46);
        Glow(context, PaneViolet, new Point(l + w * 0.72, h * 0.59), w * 0.38, h * 0.4);
        Glow(context, PaneBlue, new Point(l + w * 0.55, h * 0.35), w * 0.36, h * 0.33);
    }

    static void Glow(DrawingContext context, Color tint, Point center, double rx, double ry) {
        var brush = new RadialGradientBrush {
            GradientStops = { new GradientStop(tint, 0), new GradientStop(Color.FromArgb(0, tint.R, tint.G, tint.B), 1) },
        };
        context.DrawEllipse(brush, null, center, rx, ry);
    }
}
```

- [ ] **Step 4: Give the view model its material**

In `src/Capacitor.App/ViewModels/MainWindowViewModel.cs` add `using Capacitor.App.Materials;`, a last constructor parameter `IObservable<MaterialState>? material = null`, and:

```csharp
    SurfaceMaterial _material = SurfaceMaterial.Opaque;

    public SurfaceMaterial Material {
        get => _material;
        private set {
            this.RaiseAndSetIfChanged(ref _material, value);
            this.RaisePropertyChanged(nameof(IsGlass));
        }
    }

    public bool IsGlass => _material.IsGlass();
```

Inside the existing `this.WhenActivated(disposables => { … })` block, beside the other projections:

```csharp
            (material ?? Observable.Return(MaterialState.Opaque))
                .ObserveOn(RxSchedulers.MainThreadScheduler)
                .Subscribe(state => Material = state.Effective)
                .DisposeWith(disposables);
```

- [ ] **Step 5: Scope the window**

In `src/Capacitor.App/Views/MainWindow.axaml` add `xmlns:kcap="clr-namespace:Capacitor.App.Controls"` and change the sessions surface:

```xml
        <Grid x:Name="SessionsSurface" ColumnDefinitions="Auto,*" ClipToBounds="True"
              kcap:MaterialScope.Material="{Binding Material}"
              IsVisible="{Binding IsSessionsView}">
            <kcap:MaterialBackdrop x:Name="MaterialBackdrop" Grid.ColumnSpan="2" IsVisible="{Binding IsGlass}" />
            <views:SessionRailView x:Name="SessionRail" Grid.Column="0" />
            <Panel Grid.Column="1" ClipToBounds="True">
```

The `Panel` in column 1 and `LauncherPane` must paint no opaque background; remove one if present. The workspace host pins itself opaque and covers the backdrop:

```xml
                <ContentControl x:Name="WorkspaceHost" Content="{Binding CurrentWorkspace}"
                                kcap:MaterialScope.Material="Opaque"
                                Background="{StaticResource KcapCanvasBrush}"
```

- [ ] **Step 6: Make the rail a Surface with a styled layout**

In `src/Capacitor.App/Views/SessionRailView.axaml` add the `kcap` namespace, then replace the root `Border` (line 52) and its chrome row:

```xml
    <kcap:Surface x:Name="RailSurface" Classes="rail" BorderThickness="0,0,1,0" CornerRadius="0">
        <DockPanel Margin="0">
            <StackPanel DockPanel.Dock="Top">
                <Grid x:Name="RailChrome" Classes="railChrome" Background="Transparent"
                      PointerPressed="OnChromePointerPressed" />
```

and close it with `</kcap:Surface>`. The chrome's `Height="44"` is gone from the element: a local value would outrank the glass style.

`src/Capacitor.App/Controls/GlassRailStyles.axaml`:

```xml
<Styles xmlns="https://github.com/avaloniaui"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:kcap="clr-namespace:Capacitor.App.Controls"
        xmlns:views="clr-namespace:Capacitor.App.Views">
    <Styles.Resources>
        <SolidColorBrush x:Key="KcapGlassRailButtonBrush" Color="#183E6470" />
        <SolidColorBrush x:Key="KcapGlassRailButtonHoverBrush" Color="#303E6470" />
        <SolidColorBrush x:Key="KcapGlassRailButtonBorderBrush" Color="#44798A99" />
        <SolidColorBrush x:Key="KcapGlassRailButtonBorderHoverBrush" Color="#7E9BABBB" />
        <SolidColorBrush x:Key="KcapGlassRailRowBrush" Color="#244ED6BA" />
    </Styles.Resources>

    <Style Selector="views|SessionRailView">
        <Setter Property="Width" Value="310" />
    </Style>
    <Style Selector="views|SessionRailView Grid.railChrome">
        <Setter Property="Height" Value="44" />
    </Style>

    <!-- The panel starts below the macOS window controls. -->
    <Style Selector="views|SessionRailView[(kcap|MaterialScope.Material)=SoftGlass],
                     views|SessionRailView[(kcap|MaterialScope.Material)=LiquidGlass]">
        <Setter Property="Margin" Value="12,40,12,12" />
    </Style>
    <Style Selector="views|SessionRailView[(kcap|MaterialScope.Material)=SoftGlass] Grid.railChrome,
                     views|SessionRailView[(kcap|MaterialScope.Material)=LiquidGlass] Grid.railChrome">
        <Setter Property="Height" Value="16" />
    </Style>

    <Style Selector="views|SessionRailView[(kcap|MaterialScope.Material)=SoftGlass] Button#RailNewSessionButton /template/ ContentPresenter#PART_ContentPresenter,
                     views|SessionRailView[(kcap|MaterialScope.Material)=LiquidGlass] Button#RailNewSessionButton /template/ ContentPresenter#PART_ContentPresenter">
        <Setter Property="Background" Value="{StaticResource KcapGlassRailButtonBrush}" />
        <Setter Property="BorderBrush" Value="{StaticResource KcapGlassRailButtonBorderBrush}" />
        <Setter Property="CornerRadius" Value="10" />
    </Style>
    <Style Selector="views|SessionRailView[(kcap|MaterialScope.Material)=SoftGlass] Button#RailNewSessionButton:pointerover /template/ ContentPresenter#PART_ContentPresenter,
                     views|SessionRailView[(kcap|MaterialScope.Material)=LiquidGlass] Button#RailNewSessionButton:pointerover /template/ ContentPresenter#PART_ContentPresenter,
                     views|SessionRailView[(kcap|MaterialScope.Material)=SoftGlass] Button#RailNewSessionButton:pressed /template/ ContentPresenter#PART_ContentPresenter,
                     views|SessionRailView[(kcap|MaterialScope.Material)=LiquidGlass] Button#RailNewSessionButton:pressed /template/ ContentPresenter#PART_ContentPresenter">
        <Setter Property="Background" Value="{StaticResource KcapGlassRailButtonHoverBrush}" />
        <Setter Property="BorderBrush" Value="{StaticResource KcapGlassRailButtonBorderHoverBrush}" />
    </Style>
    <Style Selector="views|SessionRailView[(kcap|MaterialScope.Material)=SoftGlass] Button.railRow,
                     views|SessionRailView[(kcap|MaterialScope.Material)=LiquidGlass] Button.railRow">
        <Setter Property="CornerRadius" Value="9" />
    </Style>
    <Style Selector="views|SessionRailView[(kcap|MaterialScope.Material)=SoftGlass] Button.selected,
                     views|SessionRailView[(kcap|MaterialScope.Material)=LiquidGlass] Button.selected,
                     views|SessionRailView[(kcap|MaterialScope.Material)=SoftGlass] Button.holdsSelected,
                     views|SessionRailView[(kcap|MaterialScope.Material)=LiquidGlass] Button.holdsSelected">
        <Setter Property="Background" Value="{StaticResource KcapGlassRailRowBrush}" />
    </Style>
    <Style Selector="views|SessionRailView[(kcap|MaterialScope.Material)=SoftGlass] Button.railRow:pointerover /template/ ContentPresenter#PART_ContentPresenter,
                     views|SessionRailView[(kcap|MaterialScope.Material)=LiquidGlass] Button.railRow:pointerover /template/ ContentPresenter#PART_ContentPresenter,
                     views|SessionRailView[(kcap|MaterialScope.Material)=SoftGlass] Button.railRow:pressed /template/ ContentPresenter#PART_ContentPresenter,
                     views|SessionRailView[(kcap|MaterialScope.Material)=LiquidGlass] Button.railRow:pressed /template/ ContentPresenter#PART_ContentPresenter">
        <Setter Property="Background" Value="{StaticResource KcapGlassRailRowBrush}" />
    </Style>
</Styles>
```

Include it from `App.axaml` after the `GlassChipStyles.axaml` include — but only the layout styles (`Width`, `Margin`, `Grid.railChrome` `Height`) and the `RailNewSessionButton` styles stay in this file. The three ROW styles (`Button.railRow` radius, `Button.selected`/`Button.holdsSelected` fill, `Button.railRow:pointerover/:pressed` presenter fill) go at the END of `SessionRailView.axaml`'s own `UserControl.Styles`, selectors unchanged, with one comment naming the trap. Avalonia applies a control's own styles after application styles and a later frame wins an equal-priority tie, so a glass row fill declared at application level loses to the rail's opaque row styles above it; the `RailNewSessionButton` styles are unaffected only because that button carries `kcapChip`, which no rail-local style paints. `KcapGlassRailRowBrush` must stay resolvable from the moved styles; if it does not resolve, its definition moves into `SessionRailView.axaml`'s `UserControl.Resources`.

- [ ] **Step 7: Wire the service at the composition root**

In `src/Capacitor.App/App.axaml.cs`:

1. Add fields beside `_config`:

```csharp
    MaterialService? _material;
    MaterialPipelineWatch? _materialWatch;
```

2. Add a last parameter to `BuildAndShowMainWindow`: `IObservable<MaterialState>? material = null`, and pass `material: material` as the last argument of the `new MainWindowViewModel(` call inside it.

3. In `async Task StartAsync(IClassicDesktopStyleApplicationLifetime desktop)`, directly before its `BuildDaemonGraph(desktop, lane, channel, gate, profiles, laneQuiesced);` call. `BuildDaemonGraph` itself is a synchronous `void` and is where the coordinator is built, so the load happens one step earlier, where it can be awaited:

```csharp
        _material ??= await MaterialService.LoadAsync(
            new AppStateStore(_config.Path("app-state.json")), MaterialEnvironment.Detect());
        _materialWatch ??= new MaterialPipelineWatch(
            _material, h => LiquidGlassPipeline.Unavailable += h, h => LiquidGlassPipeline.Unavailable -= h,
            action => Dispatcher.UIThread.Post(action));
```

Never block on the load with `.GetAwaiter().GetResult()`: `StartAsync` runs on the UI thread. The `??=` keeps a second pass through `StartAsync` (the wizard hands over to a fresh graph) from building a second service or a second event subscription.

4. In `BuildDaemonGraph`, at the `_coordinator = new MainWindowCoordinator(() => BuildAndShowMainWindow(` site, add `material: _material?.States,` to the `BuildAndShowMainWindow(` argument list. A null source leaves the window opaque, which is also what every existing test gets.

5. Dispose both as the first two statements of `DisposeLifecycleAndServiceAsync()`, the watch BEFORE the service:

```csharp
        _materialWatch?.Dispose();
        _material?.Dispose();
```

The watch only unsubscribes; a failure report already posted to the UI thread could otherwise reach a disposed service. That method runs after the UI disposables, so no window is still subscribed to `States`.

- [ ] **Step 8: Run the tests**

Run: `dotnet build Capacitor.slnx` — expected 0 warnings, 0 errors.
Run the `MaterialWindowTests`, `MainWindowSmokeTests` and `MainWindowViewModelTests` classes — expected all pass.

- [ ] **Step 9: Commit**

```bash
/usr/bin/git -C <worktree> add src/Capacitor.App test/Capacitor.App.Tests.Unit/MaterialWindowTests.cs
/usr/bin/git -C <worktree> commit -m "Scope the sessions surface to the material with a floating rail" -m "The workspace host pins itself opaque, so a reading surface never sits on the backdrop." -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 9: Flyouts

**Outcome: the probe failed, so the FAIL path below is what the branch carries.** Glass in a
`Flyout`'s popup paints no backdrop whether the `GlassLayer` sits in the presenter's template
or wraps the flyout's content under a transparent presenter (stripe-edge contrast 4.4 inside
against 10.3–10.4 outside, pixel-identical to the opaque frame with tint, surface and
highlight zeroed), while a bare overlay-layer `Popup` with the same layer in the same window
blurs to 0.0. Flyouts stay opaque; `src/` and `test/` are byte-identical to the task's
baseline; the styles and the wrapping helper of Steps 1–2 exist only as probe-local files
(`ProbeGlassStyles.axaml`, `GlassFlyouts.cs`) under `docs/probes/2026-09-18-glass-overlay-flyout/`,
whose `Program.cs` runs both placements and the bare-popup controls in one invocation and
whose `findings.md` is the record. Steps 4–7 were not carried out. The lead for the follow-up
is a diagnostic build that instruments the vendored `LiquidGlassBackdropProvider` and
compares a `Flyout`'s popup with a bare `Popup`.

**Files:**
- Create: `docs/probes/2026-09-18-glass-overlay-flyout/Probe.csproj`, `Program.cs`, `findings.md`
- Create: `src/Capacitor.App/Controls/GlassFlyouts.cs`, `src/Capacitor.App/Controls/GlassFlyoutStyles.axaml`
- Modify: `src/Capacitor.App/App.axaml`, `src/Capacitor.App/Views/LauncherPaneView.axaml.cs`, `src/Capacitor.App/Views/MainWindow.axaml.cs`, `src/Capacitor.App/Views/SessionRailView.axaml.cs`
- Test: `test/Capacitor.App.Tests.Unit/GlassFlyoutTests.cs`

**Interfaces:**
- Consumes: `GlassLayer` (`Kind="Panel"`), `MaterialScope`, `SurfaceMaterials.IsGlass`, `LiquidGlassPipeline.Unavailable`, `LiquidGlassDiagnostics.Snapshot`.
- Produces: `GlassFlyouts.FollowMaterial(PopupFlyoutBase flyout, Control owner)`.

- [ ] **Step 1: Write the styles and the helper the probe exercises**

`src/Capacitor.App/Controls/GlassFlyoutStyles.axaml`. Two templates sharing only the layer: a `MenuFlyoutPresenter` presents items, not content.

```xml
<Styles xmlns="https://github.com/avaloniaui"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:kcap="clr-namespace:Capacitor.App.Controls"
        xmlns:glass="clr-namespace:LiquidGlassAvaloniaUI;assembly=LiquidGlassAvaloniaUI">
    <Style Selector="FlyoutPresenter.kcapPanel[(kcap|MaterialScope.Material)=SoftGlass],
                     FlyoutPresenter.kcapPanel[(kcap|MaterialScope.Material)=LiquidGlass]">
        <Setter Property="Background" Value="Transparent" />
        <Setter Property="BorderBrush" Value="Transparent" />
        <Setter Property="Template">
            <ControlTemplate>
                <Panel glass:LiquidGlassBackdrop.IsExcludedFromCapture="True">
                    <kcap:GlassLayer Kind="Panel" CornerRadius="{TemplateBinding CornerRadius}" />
                    <Border CornerRadius="{TemplateBinding CornerRadius}" Padding="{TemplateBinding Padding}"
                            ClipToBounds="True">
                        <ContentPresenter Name="PART_ContentPresenter" Content="{TemplateBinding Content}"
                                          ContentTemplate="{TemplateBinding ContentTemplate}" />
                    </Border>
                </Panel>
            </ControlTemplate>
        </Setter>
    </Style>

    <Style Selector="MenuFlyoutPresenter.kcapPanel[(kcap|MaterialScope.Material)=SoftGlass],
                     MenuFlyoutPresenter.kcapPanel[(kcap|MaterialScope.Material)=LiquidGlass]">
        <Setter Property="Background" Value="Transparent" />
        <Setter Property="BorderBrush" Value="Transparent" />
        <Setter Property="Template">
            <ControlTemplate>
                <Panel glass:LiquidGlassBackdrop.IsExcludedFromCapture="True">
                    <kcap:GlassLayer Kind="Panel" CornerRadius="{TemplateBinding CornerRadius}" />
                    <Border CornerRadius="{TemplateBinding CornerRadius}" Padding="{TemplateBinding Padding}"
                            ClipToBounds="True">
                        <ScrollViewer>
                            <ItemsPresenter Name="PART_ItemsPresenter" ItemsPanel="{TemplateBinding ItemsPanel}"
                                            KeyboardNavigation.TabNavigation="Continue" Grid.IsSharedSizeScope="True" />
                        </ScrollViewer>
                    </Border>
                </Panel>
            </ControlTemplate>
        </Setter>
    </Style>
</Styles>
```

`src/Capacitor.App/Controls/GlassFlyouts.cs`:

```csharp
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Capacitor.App.Materials;

namespace Capacitor.App.Controls;

public static class GlassFlyouts {
    /// The backdrop snapshot is per top-level window: a flyout in its own native popup window sees
    /// only itself. Under glass the popup is hosted in the owner's window instead, which clips it
    /// to that window; under Opaque it stays a native popup.
    public static void FollowMaterial(PopupFlyoutBase flyout, Control owner) =>
        flyout.Opening += (_, _) => flyout.Popup.ShouldUseOverlayLayer = MaterialScope.GetMaterial(owner).IsGlass();
}
```

Do not include the styles from `App.axaml` yet: the probe decides that.

- [ ] **Step 2: Write the probe**

`docs/probes/2026-09-18-glass-overlay-flyout/Probe.csproj` (not in `Capacitor.slnx`; package versions come from the central pins):

```xml
<Project Sdk="Microsoft.NET.Sdk">
    <PropertyGroup>
        <OutputType>Exe</OutputType>
        <TargetFramework>net10.0</TargetFramework>
        <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
        <IsPackable>false</IsPackable>
        <!-- A probe, not shipped and not in the solution: top-level types and console output
             are the point, so the repo's analyzers and doc-file rule are off here. -->
        <GenerateDocumentationFile>false</GenerateDocumentationFile>
        <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
        <EnforceCodeStyleInBuild>false</EnforceCodeStyleInBuild>
    </PropertyGroup>
    <ItemGroup>
        <ProjectReference Include="..\..\..\src\Capacitor.App\Capacitor.App.csproj" />
        <PackageReference Include="Avalonia.Headless" />
        <PackageReference Include="Avalonia.Skia" />
        <PackageReference Include="Avalonia.Themes.Fluent" />
    </ItemGroup>
</Project>
```

`docs/probes/2026-09-18-glass-overlay-flyout/Program.cs`:

```csharp
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.Controls;
using Capacitor.App.Materials;
using LiquidGlassAvaloniaUI;

sealed class ProbeApp : Application {
    static readonly Uri Base = new("avares://Kurrent Capacitor/");

    public override void Initialize() {
        RequestedThemeVariant = ThemeVariant.Dark;
        Resources.MergedDictionaries.Add(new ResourceInclude(Base) { Source = new Uri("avares://Kurrent Capacitor/Controls/GlassLayer.axaml") });
        Styles.Add(new FluentTheme());
        Styles.Add(new StyleInclude(Base) { Source = new Uri("avares://Kurrent Capacitor/Controls/GlassStyles.axaml") });
        Styles.Add(new StyleInclude(Base) { Source = new Uri("avares://Kurrent Capacitor/Controls/GlassFlyoutStyles.axaml") });
    }
}

// Hard edges every 16 px: blur is measurable as lost edge contrast.
sealed class Stripes : Control {
    static readonly IBrush A = new SolidColorBrush(Color.Parse("#20C0A0"));
    static readonly IBrush B = new SolidColorBrush(Color.Parse("#101020"));

    public override void Render(DrawingContext context) {
        for (var x = 0.0; x < Bounds.Width; x += 16)
            context.FillRectangle((int)(x / 16) % 2 == 0 ? A : B, new Rect(x, 0, 16, Bounds.Height));
    }
}

static class Program {
    const int W = 480, H = 320;
    static int _stride;
    static string? _unavailable;

    static int Main() {
        AppBuilder.Configure<ProbeApp>().UseSkia()
            // UseHeadlessDrawing selects the dummy backend; Skia only draws with it off.
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();
        LiquidGlassPipeline.Unavailable += reason => _unavailable = reason;

        var ok = Run("Flyout", text => new Flyout { Content = Body(text) })
               & Run("MenuFlyout", text => new MenuFlyout { Items = { new MenuItem { Header = text }, new MenuItem { Header = "second" } } });

        var diagnostics = LiquidGlassDiagnostics.Snapshot;
        Console.WriteLine($"pipeline unavailable: {_unavailable ?? "no"}; captures published: {diagnostics.CapturesPublished}");
        ok &= _unavailable is null && diagnostics.CapturesPublished > 0;
        Console.WriteLine(ok ? "PASS" : "FAIL");
        return ok ? 0 : 1;
    }

    static Control Body(string text) => new TextBlock { Text = text, Foreground = Brushes.White, Width = 300, Height = 120 };

    static bool Run(string name, Func<string, PopupFlyoutBase> build) {
        var glass = Capture(build("alpha"), hideLayer: false, out var material, out var region);
        var clear = Capture(build("alpha"), hideLayer: true, out _, out _);
        var other = Capture(build("omega!!"), hideLayer: false, out _, out _);

        var row = region.Y + region.Height / 2;
        var outside = Gradient(glass, H - 20, region.X, region.Right);
        var inside = Gradient(glass, row, region.X + 20, region.Right - 20);
        var differs = Diff(glass, clear, region);
        // The lower third holds no text in either frame: a blurred ghost of the text would show here.
        var ghost = Diff(glass, other, new PixelRect(region.X + 20, region.Y + region.Height * 2 / 3, region.Width - 40, region.Height / 3 - 10));

        Console.WriteLine($"{name}: material={material} region={region} edge outside={outside:F1} inside={inside:F1} vs-transparent={differs}px text-ghost={ghost}px");
        return material == SurfaceMaterial.SoftGlass && differs > region.Width * region.Height / 2 && inside < outside / 4 && ghost == 0;
    }

    static byte[] Capture(PopupFlyoutBase flyout, bool hideLayer, out SurfaceMaterial material, out PixelRect region) {
        flyout.FlyoutPresenterClasses.Add("kcapPanel");
        var owner = new Button { Content = "open", Flyout = flyout, Margin = new Thickness(24) };
        var scope = new Panel { Children = { new Stripes(), owner } };
        MaterialScope.SetMaterial(scope, SurfaceMaterial.SoftGlass);
        GlassFlyouts.FollowMaterial(flyout, owner);
        var window = new Window { Width = W, Height = H, Content = scope };
        window.Show();
        flyout.ShowAt(owner);
        Pump();

        var presenter = (Control)flyout.Popup.Child!;
        if (presenter is TemplatedControl templated) templated.CornerRadius = new CornerRadius(12);
        material = MaterialScope.GetMaterial(presenter);
        if (hideLayer) presenter.GetVisualDescendants().OfType<GlassLayer>().Single().IsVisible = false;
        Pump();

        var origin = presenter.TranslatePoint(default, window)!.Value;
        region = new PixelRect((int)origin.X, (int)origin.Y, (int)presenter.Bounds.Width, (int)presenter.Bounds.Height);
        if (!flyout.Popup.IsUsingOverlayLayer) throw new InvalidOperationException("the popup is not in the overlay layer");

        using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("no frame");
        using var buffer = frame.Lock();
        _stride = buffer.RowBytes;
        var bytes = new byte[buffer.RowBytes * buffer.Size.Height];
        Marshal.Copy(buffer.Address, bytes, 0, bytes.Length);
        flyout.Hide();
        window.Close();
        Pump();
        return bytes;
    }

    static void Pump() {
        for (var i = 0; i < 12; i++) {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    static int Diff(byte[] a, byte[] b, PixelRect r) {
        var count = 0;
        for (var y = r.Y; y < r.Bottom; y++)
            for (var x = r.X; x < r.Right; x++) {
                var i = y * _stride + x * 4;
                if (Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2]) > 6) count++;
            }
        return count;
    }

    static double Gradient(byte[] a, int y, int x0, int x1) {
        double sum = 0;
        for (var x = x0 + 1; x < x1; x++) sum += Math.Abs(a[y * _stride + x * 4 + 1] - a[y * _stride + (x - 1) * 4 + 1]);
        return sum / (x1 - x0 - 1);
    }
}
```

- [ ] **Step 3: Run the probe and record it**

Run: `dotnet run --project docs/probes/2026-09-18-glass-overlay-flyout/Probe.csproj`
Expected: two result lines, a pipeline line and `PASS`, with `edge inside` under a quarter of `edge outside`, `vs-transparent` over half the region and `text-ghost=0px`.

Write `docs/probes/2026-09-18-glass-overlay-flyout/findings.md`:

```markdown
# Glass in overlay-layer flyouts

**Question.** A `LiquidGlassSurface` samples a snapshot of its own top-level window. Does a
`kcapPanel` flyout, hosted in the overlay layer of the owner's window, refract the app behind
it — for a `Flyout` and for a `MenuFlyout`?

**Method.** `Program.cs` here: Skia renderer, headless drawing off, a window of hard-edged
16 px stripes, each flyout opened under a Soft glass scope through the app's own
`GlassFlyoutStyles.axaml` and `GlassFlyouts.FollowMaterial`. Four checks per presenter type:
the inherited material, a frame that differs from the same flyout with its glass layer hidden,
lost stripe-edge contrast inside the panel, and no change outside the text when the text changes.

**Result.**
```

then paste the probe's output lines verbatim in a fenced block, and end with one sentence: the verdict (`PASS` or `FAIL`) and what ships because of it.

If the probe prints `FAIL`: stop here, skip Steps 4–7, keep `GlassFlyoutStyles.axaml` out of `App.axaml`, delete the `FollowMaterial` call sites, record the failing line in `findings.md`, and report. Flyouts then stay opaque in this change.

- [ ] **Step 4: Write the failing tests**

`test/Capacitor.App.Tests.Unit/GlassFlyoutTests.cs`:

```csharp
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.Controls;
using Capacitor.App.Materials;
using LiquidGlassAvaloniaUI;

namespace Capacitor.App.Tests.Unit;

[NotInParallel("AvaloniaSession")]
public class GlassFlyoutTests {
    static (Window Window, Button Owner) Open(PopupFlyoutBase flyout, SurfaceMaterial material) {
        flyout.FlyoutPresenterClasses.Add("kcapPanel");
        var owner = new Button { Content = "open", Flyout = flyout };
        var scope = new Panel { Children = { owner } };
        MaterialScope.SetMaterial(scope, material);
        GlassFlyouts.FollowMaterial(flyout, owner);
        var window = new Window { Content = scope, Width = 480, Height = 320 };
        window.Show();
        flyout.ShowAt(owner);
        Dispatcher.UIThread.RunJobs();
        return (window, owner);
    }

    [Test]
    public Task Under_glass_a_panel_flyout_moves_into_the_window_and_draws_a_panel_layer() => AvaloniaSession.RunOnUiAsync(async () => {
        var flyout = new Flyout { Content = new TextBlock { Text = "rows" } };
        var (window, _) = Open(flyout, SurfaceMaterial.SoftGlass);
        try {
            var presenter = (Control)flyout.Popup.Child!;
            var layer = presenter.GetVisualDescendants().OfType<GlassLayer>().Single();
            await Assert.That(flyout.Popup.ShouldUseOverlayLayer).IsTrue();
            await Assert.That(layer.Kind).IsEqualTo(GlassKind.Panel);
            await Assert.That(LiquidGlassBackdrop.GetIsExcludedFromCapture((Visual)layer.GetVisualParent()!)).IsTrue();
        } finally { flyout.Hide(); window.Close(); }
    });

    [Test]
    public Task Under_opaque_a_panel_flyout_stays_a_native_popup_without_glass() => AvaloniaSession.RunOnUiAsync(async () => {
        var flyout = new Flyout { Content = new TextBlock { Text = "rows" } };
        var (window, _) = Open(flyout, SurfaceMaterial.Opaque);
        try {
            var presenter = (Control)flyout.Popup.Child!;
            await Assert.That(flyout.Popup.ShouldUseOverlayLayer).IsFalse();
            await Assert.That(presenter.GetVisualDescendants().OfType<GlassLayer>().Any()).IsFalse();
        } finally { flyout.Hide(); window.Close(); }
    });

    [Test]
    public Task Under_glass_a_menu_flyout_still_presents_its_items() => AvaloniaSession.RunOnUiAsync(async () => {
        var flyout = new MenuFlyout { Items = { new MenuItem { Header = "Documentation" }, new MenuItem { Header = "Report a bug…" }, new MenuItem { Header = "Send feedback…" } } };
        var (window, _) = Open(flyout, SurfaceMaterial.LiquidGlass);
        try {
            var presenter = (Control)flyout.Popup.Child!;
            await Assert.That(presenter.GetVisualDescendants().OfType<MenuItem>().Count()).IsEqualTo(3);
            await Assert.That(presenter.GetVisualDescendants().OfType<GlassLayer>().Count()).IsEqualTo(1);
        } finally { flyout.Hide(); window.Close(); }
    });
}
```

- [ ] **Step 5: Run to verify failure**

Run the `GlassFlyoutTests` class.
Expected: the two glass tests FAIL with no `GlassLayer`, because `App.axaml` does not include the styles yet.

- [ ] **Step 6: Ship the styles and attach the seven flyouts**

In `App.axaml`, after the `GlassRailStyles.axaml` include:

```xml
        <StyleInclude Source="avares://Kurrent Capacitor/Controls/GlassFlyoutStyles.axaml" />
```

In `src/Capacitor.App/Views/LauncherPaneView.axaml.cs` add `using Capacitor.App.Controls;`. `PanelFlyout` builds the shared picker flyout; give it the owner and attach there, so its callers need no second line:

```csharp
    static Flyout PanelFlyout(Control owner, Control content, double minWidth) {
        var host = new Border { Child = content, MinWidth = minWidth };
        var flyout = new Flyout {
            Placement = PlacementMode.Bottom, Content = host,
            // A few pixels of air between the chip and the panel — flush looks glued on.
            VerticalOffset = 6,
        };
        flyout.FlyoutPresenterClasses.Add("kcapPanel");
        GlassFlyouts.FollowMaterial(flyout, owner);
        return flyout;
    }
```

Every `PanelFlyout(` call passes `this` as the new first argument. The harness picker builds its own `Flyout` (the `new Flyout { Placement = PlacementMode.Bottom, Content = root, VerticalOffset = 6 }` block): add `GlassFlyouts.FollowMaterial(flyout, this);` directly after its `FlyoutPresenterClasses.Add("kcapPanel");`.

In `src/Capacitor.App/Views/MainWindow.axaml.cs`, at the end of the constructor:

```csharp
        if (this.FindControl<Button>("ActivityButton") is { Flyout: PopupFlyoutBase activity } activityButton)
            GlassFlyouts.FollowMaterial(activity, activityButton);
```

In `src/Capacitor.App/Views/SessionRailView.axaml.cs`, at the end of the constructor:

```csharp
        if (this.FindControl<Button>("RailHelpButton") is { Flyout: PopupFlyoutBase help } helpButton)
            GlassFlyouts.FollowMaterial(help, helpButton);
```

Both files need `using Avalonia.Controls.Primitives;` and `using Capacitor.App.Controls;`. Confirm nothing else uses the class: `rtk proxy grep -rn 'kcapPanel' src/Capacitor.App` must list only `App.axaml`, `GlassFlyoutStyles.axaml` and these three views.

- [ ] **Step 7: Run the tests**

Run: `dotnet build Capacitor.slnx` — expected 0 warnings, 0 errors.
Run the `GlassFlyoutTests`, `MainWindowSmokeTests` and `HomeViewSmokeTests` classes — expected all pass, including the existing help-menu test.

- [ ] **Step 8: Commit**

```bash
/usr/bin/git -C <worktree> add docs/probes/2026-09-18-glass-overlay-flyout src/Capacitor.App test/Capacitor.App.Tests.Unit/GlassFlyoutTests.cs
/usr/bin/git -C <worktree> commit -m "Draw panel flyouts in glass inside the owner's window" -m "A native popup window has its own backdrop snapshot and cannot see the app, so under glass the popup moves to the overlay layer." -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 10: Appearance in Settings

**Files:**
- Modify: `src/Capacitor.App/ViewModels/SettingsViewModel.cs`, `src/Capacitor.App/Views/SettingsWindow.axaml`, `src/Capacitor.App/App.axaml.cs` (`OpenSettings`)
- Test: `test/Capacitor.App.Tests.Unit/SettingsMaterialTests.cs`

**Interfaces:**
- Consumes: `IMaterialService`, `MaterialState`, `MaterialAvailability`, `Surface`.
- Produces: `SettingsViewModel(…, IMaterialService? material = null)` (new last parameter) with `IsOpaque`, `IsSoftGlass`, `IsLiquidGlass` (two-way `bool`), `MaterialChoicesEnabled : bool`, `MaterialHint : string?`; controls `MaterialOpaque`, `MaterialSoftGlass`, `MaterialLiquidGlass`, `MaterialHintText`.

- [ ] **Step 1: Write the failing tests**

`test/Capacitor.App.Tests.Unit/SettingsMaterialTests.cs`:

```csharp
using Avalonia.Controls;
using Avalonia.Threading;
using Capacitor.App.Materials;
using Capacitor.App.Services;
using Capacitor.App.Services.Mutation;
using Capacitor.App.ViewModels;
using Capacitor.App.Views;
using Capacitor.Cli.Core.Config;

namespace Capacitor.App.Tests.Unit;

[NotInParallel("AvaloniaSession")]
public class SettingsMaterialTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    static readonly MaterialEnvironment Mac = new(GlassCapable: true, ReduceTransparency: false);

    SettingsViewModel Build(IMaterialService material) {
        ConfigMutator.Mutate(Config.Root, c => c with { Profiles = new() {
            ["work"] = new Profile { ServerUrl = "https://work.example", Daemon = new DaemonSettings { Name = "daemon-a", MaxAgents = 5 } }
        } });
        return new SettingsViewModel(new SettingsProfileStore(Config.Root, "work", "https://work.example"),
            new FakeDaemonClientService(), new ScriptedLocalControlOps(), (_, _) => Task.FromResult(false),
            (_, _) => Task.FromResult<MutationOutcome>(new MutationOutcome.Succeeded()),
            (_, _) => Task.FromResult(false), _ => Task.FromResult(false), true, Task.CompletedTask,
            (_, _) => Task.FromResult(true), material: material);
    }

    [Test]
    public Task Picking_a_material_persists_it_and_moves_the_selection() => AvaloniaSession.RunOnUiAsync(async () => {
        var store = new InMemoryAppStateStore();
        using var service = new MaterialService(store, Mac, requested: null);
        using var vm = Build(service);
        await Assert.That(vm.IsSoftGlass).IsTrue();
        await Assert.That(vm.MaterialChoicesEnabled).IsTrue();
        await Assert.That(vm.MaterialHint).IsNull();

        vm.IsLiquidGlass = true;
        Dispatcher.UIThread.RunJobs();

        await Assert.That(store.State.Material).IsEqualTo("liquid_glass");
        await Assert.That(vm.IsLiquidGlass).IsTrue();
        await Assert.That(vm.IsSoftGlass).IsFalse();
    });

    [Test]
    public Task A_machine_that_cannot_do_glass_disables_the_choices_and_says_why() => AvaloniaSession.RunOnUiAsync(async () => {
        using var service = new MaterialService(new InMemoryAppStateStore(), new MaterialEnvironment(false, false), requested: null);
        using var vm = Build(service);
        await Assert.That(vm.IsOpaque).IsTrue();
        await Assert.That(vm.MaterialChoicesEnabled).IsFalse();
        await Assert.That(vm.MaterialHint!).Contains("macOS");
    });

    [Test]
    public Task A_view_model_created_after_a_failure_shows_the_reason() => AvaloniaSession.RunOnUiAsync(async () => {
        using var service = new MaterialService(new InMemoryAppStateStore(), Mac, SurfaceMaterial.SoftGlass);
        service.ReportPipelineFailure("shader did not compile");
        using var vm = Build(service);
        await Assert.That(vm.IsOpaque).IsTrue();
        await Assert.That(vm.MaterialChoicesEnabled).IsFalse();
        await Assert.That(vm.MaterialHint!).Contains("shader did not compile");
    });

    [Test]
    public Task Reduce_transparency_explains_the_opaque_default_and_leaves_the_choices_on() => AvaloniaSession.RunOnUiAsync(async () => {
        using var service = new MaterialService(new InMemoryAppStateStore(), Mac with { ReduceTransparency = true }, requested: null);
        using var vm = Build(service);
        await Assert.That(vm.IsOpaque).IsTrue();
        await Assert.That(vm.MaterialChoicesEnabled).IsTrue();
        await Assert.That(vm.MaterialHint!).Contains("Reduce transparency");
    });

    [Test]
    public Task The_window_binds_the_three_choices_and_the_hint() => AvaloniaSession.RunOnUiAsync(async () => {
        using var service = new MaterialService(new InMemoryAppStateStore(), Mac, SurfaceMaterial.Opaque);
        using var vm = Build(service);
        var window = new SettingsWindow { DataContext = vm };
        try {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            await Assert.That(window.FindControl<RadioButton>("MaterialOpaque")!.IsChecked).IsTrue();
            await Assert.That(window.FindControl<RadioButton>("MaterialSoftGlass")!.Classes.Contains("kcapChoice")).IsTrue();
            await Assert.That(window.FindControl<RadioButton>("MaterialLiquidGlass")!.IsEffectivelyEnabled).IsTrue();
            await Assert.That(window.FindControl<TextBlock>("MaterialHintText")!.IsVisible).IsFalse();
        } finally { window.Close(); }
    });
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet build Capacitor.slnx`
Expected: FAIL, `SettingsViewModel` has no `material` parameter.

- [ ] **Step 3: Extend the view model**

In `src/Capacitor.App/ViewModels/SettingsViewModel.cs` add `using Capacitor.App.Materials;`, a last constructor parameter `IMaterialService? material = null`, a field `readonly IMaterialService? _material;` and `MaterialState _materialState = MaterialState.Opaque;`. In the constructor, after the other assignments:

```csharp
        _material = material;
        if (material is not null) {
            _materialState = material.Current;
            material.States.ObserveOn(RxSchedulers.MainThreadScheduler).Subscribe(state => {
                _materialState = state;
                this.RaisePropertyChanged(nameof(IsOpaque));
                this.RaisePropertyChanged(nameof(IsSoftGlass));
                this.RaisePropertyChanged(nameof(IsLiquidGlass));
                this.RaisePropertyChanged(nameof(MaterialChoicesEnabled));
                this.RaisePropertyChanged(nameof(MaterialHint));
            }).DisposeWith(_subscriptions);
        }
```

and the members:

```csharp
    public bool IsOpaque {
        get => _materialState.Effective == SurfaceMaterial.Opaque;
        set { if (value) Choose(SurfaceMaterial.Opaque); }
    }

    public bool IsSoftGlass {
        get => _materialState.Effective == SurfaceMaterial.SoftGlass;
        set { if (value) Choose(SurfaceMaterial.SoftGlass); }
    }

    public bool IsLiquidGlass {
        get => _materialState.Effective == SurfaceMaterial.LiquidGlass;
        set { if (value) Choose(SurfaceMaterial.LiquidGlass); }
    }

    public bool MaterialChoicesEnabled => _material is not null && _materialState.Availability == MaterialAvailability.Available;

    public string? MaterialHint => _materialState switch {
        { Availability: MaterialAvailability.NotCapable } => "Glass materials need macOS.",
        { Availability: MaterialAvailability.PipelineFailed } failed => $"Glass is off until the next launch: {failed.FailureReason}.",
        { Requested: null, ReduceTransparency: true } => "Opaque because Reduce transparency is on. Picking a glass material overrides it.",
        _ => null,
    };

    // A radio only ever reports the one that turned on; SetAsync never throws, the store swallows a failed write.
    void Choose(SurfaceMaterial material) {
        if (_material is null || material == _materialState.Effective) return;
        _ = _material.SetAsync(material);
    }
```

- [ ] **Step 4: Add the Appearance card**

In `src/Capacitor.App/Views/SettingsWindow.axaml` add the `kcap` namespace. The window's header block (the `StackPanel` in grid row 0 holding `DaemonTitleText`, `StatusChip` and the subtitle) moves into the `ScrollViewer`'s `StackPanel` as the daemon group's heading, keeping every `x:Name`; the root grid becomes `RowDefinitions="*,Auto"` with the `ScrollViewer` in row 0 (drop its `Margin`'s top value) and `MessageText` in row 1. At the top of that `StackPanel`, before the moved heading:

```xml
                <StackPanel Spacing="10">
                    <TextBlock x:Name="AppearanceTitleText" Classes="kcapTitle" Text="Appearance" LineHeight="22" />
                    <kcap:Surface CornerRadius="12" Padding="20">
                        <StackPanel Spacing="12">
                            <StackPanel Spacing="6">
                                <TextBlock Classes="kcapLabel" Text="Material" />
                                <TextBlock Classes="kcapHint" TextWrapping="Wrap"
                                           Text="How the session rail, the launcher and its menus are drawn. Reading surfaces stay opaque." />
                            </StackPanel>
                            <StackPanel Orientation="Horizontal" Spacing="8" IsEnabled="{Binding MaterialChoicesEnabled}">
                                <RadioButton x:Name="MaterialOpaque" Classes="kcapChoice" GroupName="Material"
                                             Content="Opaque" IsChecked="{Binding IsOpaque}" />
                                <RadioButton x:Name="MaterialSoftGlass" Classes="kcapChoice" GroupName="Material"
                                             Content="Soft glass" IsChecked="{Binding IsSoftGlass}" />
                                <RadioButton x:Name="MaterialLiquidGlass" Classes="kcapChoice" GroupName="Material"
                                             Content="Liquid glass" IsChecked="{Binding IsLiquidGlass}" />
                            </StackPanel>
                            <TextBlock x:Name="MaterialHintText" Classes="kcapHint" TextWrapping="Wrap"
                                       Text="{Binding MaterialHint}"
                                       IsVisible="{Binding MaterialHint, Converter={x:Static StringConverters.IsNotNullOrEmpty}}" />
                        </StackPanel>
                    </kcap:Surface>
                </StackPanel>
```

- [ ] **Step 5: Pass the service when Settings opens**

In `App.axaml.cs`, `OpenSettings`: add `material: _material` as the last argument of `new SettingsViewModel(`.

- [ ] **Step 6: Run the tests**

Run: `dotnet build Capacitor.slnx` — expected 0 warnings, 0 errors.
Run the `SettingsMaterialTests`, `SettingsWindowSmokeTests` and `SettingsViewModelTests` classes — expected all pass.

- [ ] **Step 7: Commit**

```bash
/usr/bin/git -C <worktree> add src/Capacitor.App test/Capacitor.App.Tests.Unit/SettingsMaterialTests.cs
/usr/bin/git -C <worktree> commit -m "Choose the surface material in Settings" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 11: Docs and the final check

**Files:**
- Modify: `docs/CHANGES.md`, `README.md`

- [ ] **Step 1: Add the change note**

In `docs/CHANGES.md`, as the first `##` entry:

```markdown
## The desktop app has a surface material

Opaque, Soft glass or Liquid glass, chosen under Settings → Appearance. Material is a second axis
beside palette, not a theme variant: `ThemeVariant` stays pinned to `Dark`, so a light palette
later does not multiply the glass variants. An inherited `MaterialScope.Material` property carries
it down the logical tree, which is how a flyout takes its opener's material and how the workspace
host pins itself opaque: glass is for navigation and controls, never for a reading surface.

Glass is a control, not a brush — a brush cannot sample what is behind it — so a card is a
`Surface` whose template changes, and all glass drawing sits in one `GlassLayer`. The chat view's
own cards stay `Border`s: two are rows of the virtualised list, where a templated control triples
the visuals, and the view is always opaque. A `Surface`'s content joins the visual tree on first
measure rather than on assignment, so content under a collapsed ancestor is reached through the
name scope, never by a visual-tree walk. Three traps shaped it. Content drawn beside the glass is captured into the glass's own backdrop and blurred under
itself, so every glass template's root sets `IsExcludedFromCapture`. The backdrop snapshot is per
top-level window, so under glass a panel flyout moves into the owner's window
(`Popup.ShouldUseOverlayLayer`); a native popup would refract only itself. And Fluent's per-state
`Button` fills target `PART_ContentPresenter` and survive a `Template` swap, so the glass chip names
its presenter `ChipContent`, as `RadioButton.kcapChoice` already does.

`LiquidGlassAvaloniaUI` is vendored as source under `src/ThirdParty/` because it is not on
NuGet.org. It reports nothing when its shader pipeline cannot run, so the copy carries one patch,
`LiquidGlassPipeline.Unavailable`; on it the app latches Opaque for the session and keeps the stored
choice. The opaque template holds no glass element, so that fallback cannot itself fail. Soft glass
is the default on macOS unless "Reduce transparency" is on; an explicit choice overrides the flag.
```

- [ ] **Step 2: Add the README paragraph**

In `README.md`, under `### Desktop app (macOS)`, after the paragraph that begins "Open **Settings…**":

```markdown
**Appearance** in the same window sets the material of the session rail, the launcher and its menus: **Opaque**, **Soft glass** or **Liquid glass**. It applies at once. Soft glass is the default unless macOS **Reduce transparency** is on; picking a glass material overrides that. Sessions, the pull request reader and every other window stay opaque. If the glass renderer cannot start, the app uses Opaque until the next launch and the Appearance card says why.
```

- [ ] **Step 3: Verify the whole change**

Run: `dotnet build Capacitor.slnx`
Expected: 0 warnings, 0 errors.

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj`
Expected: every test passes.

Run: `/usr/bin/git -C <worktree> status --short`
Expected: only the two docs files modified; nothing under `src/ThirdParty/` beyond Task 1.

- [ ] **Step 4: Commit**

```bash
/usr/bin/git -C <worktree> add docs/CHANGES.md README.md
/usr/bin/git -C <worktree> commit -m "Document the desktop surface material" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

- [ ] **Step 5: Hand over the manual check**

The sandbox cannot launch the GUI, so report this list to the user rather than ticking it. On macOS, `dotnet run --project src/Capacitor.App/Capacitor.App.csproj`, then under Settings → Appearance:

1. Each of the three materials applies live to the rail, the goal card and the five launcher chips; the goal text and picker selections survive a switch.
2. Under glass the rail floats below the window controls with its footer visible; the window still drags by the strip above it and by the launcher's header.
3. Each chip picker, the Activity panel and the rail's help menu open as glass panels, stay inside the window, dismiss on outside click, and the help menu works from the keyboard.
4. Opening a session covers the backdrop with an opaque workspace; closing it brings the launcher back over the glow.
5. Dragging a file over the goal card shows the primary-colour rim under both materials.
6. With System Settings → Accessibility → Display → Reduce transparency on and no stored choice, the app starts Opaque with the hint shown.
7. Onboarding, Feedback and the Settings window itself look exactly as before.

