# GitHub-Flavoured Markdown in the Pull Request Reader — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the desktop pull request reader render the HTML subset review bots write — `<details>`, `<pre>`, inline formatting tags, `<img>`, `<a>`, `<br>`, entities — plus GitHub alert blocks, instead of showing the markup as text, while chat keeps today's rendering.

**Architecture:** A Markdig `DocumentProcessed` pass (`Capacitor.App.GitHubHtml`, no Avalonia dependency) rewrites the HTML it understands into standard Markdig nodes, so MarkView's existing renderers and the app's `LinkPolicy` link renderer apply unchanged; only `<details>` and `<pre>` get node types and renderers of their own. `MarkdownView` gains a `Flavor`; the GitHub flavor selects a pipeline that carries the pass and a GitHub-mode `KcapMarkdownExtension`, and owns per-view details state that re-renders on toggle.

**Tech Stack:** .NET 10, Avalonia 12, MarkView.Avalonia 12.2.1, Markdig 1.3.2, TUnit on Microsoft Testing Platform, headless Avalonia tests via `AvaloniaSession`.

**Spec:** `docs/superpowers/specs/2026-09-17-ai2889-github-markdown-pr-reader-design.md` (kcap-cli#982; Codex spec review clean after eight rounds). Read it first: every rule below is argued there, and "Current state" records the MarkView, Markdig and Avalonia behaviour the design leans on.

## Global Constraints

- **Worktree:** run every command from the repository root of the worktree this plan lives in. Branch from an up-to-date `origin/main` (`git fetch origin main` first — main moves fast).
- **Scope is `src/Capacitor.App` and `test/Capacitor.App.Tests.Unit` only.** No CLI surface changes, so `README.md` is not touched.
- **Namespace:** the pass lives in `src/Capacitor.App/GitHubHtml/`, namespace `Capacitor.App.GitHubHtml`. Never name a namespace `Capacitor.App.Markdown`: it hides Markdig's `Markdown` class from every file under `Capacitor.App`.
- **One type per file, named after the type.** Private nested classes inside a renderer host (as `KcapMarkdownExtension` already has) are fine.
- **Comments are scarce:** no ticket ids, no design coordinates ("D4", "Task 3"), no change narration ("previously", "no longer"). A comment names a trap or a deliberate constraint, in one or two lines, or is not written. Do not imitate the density of comments already in the tree.
- **No Linear ids in `.cs` files or commit messages.** The GitHub issue is `#982`.
- **Limits, verbatim from the spec:** `MaxDepth` = 100 (a node's depth is the number of containers above it; Markdig's renderer throws past 128); details nesting deeper than 8 is not folded; a link under more than 8 emphasis ancestors is not hoisted.
- **Never a `\r` or `\n` inside a `Run`.** Avalonia's line breaker does not finish laying one out under a height-unconstrained parent. Line structure is `LineBreak` inlines.
- **Nothing is fetched on render.** `renderer.ImageLoaders` stays cleared in both flavors.
- **Degrade rule:** unmatched or rejected HTML renders as its source text — never nothing, never a guess. HTML comments inside converted content are the one exception.
- **Copy:** a details section with no summary reads `Details`; an image with no usable label reads `image`.
- **Tests:** TUnit on MTP. Run one project, always filtered:
  `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/<Class>/*"` (never `--filter`). Tree-level tests need no Avalonia session and carry no parallel constraint. View tests carry `[NotInParallel("AvaloniaSession")]` and do their work inside `AvaloniaSession.RunOnUiAsync`. Never assert a chat-flavor bug into place.
- **Build:** `dotnet build src/Capacitor.App/Capacitor.App.csproj` must end with 0 warnings (warnings are errors; Avalonia `AVLN` XAML warnings count). The CLI build command does not build the desktop app.
- **Commits:** subject is one imperative clause, at most 80 characters including the trailing ` (#982)`; body optional, at most five lines, naming a constraint the diff does not show; last line `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`. Stage by explicit path. Every subject must stand on its own — the branch is squash-merged.

---

## File structure

**`src/Capacitor.App/GitHubHtml/`** — the pass; Markdig only.
- `HtmlTokenKind.cs`, `HtmlToken.cs`, `HtmlTokenization.cs`, `HtmlTokenizer.cs` — one forward scan from a string to tags, text and comments.
- `HtmlTagClass.cs`, `HtmlTags.cs` — the allow-list: void, structural, paired; emphasis and code mappings.
- `InlineText.cs` — whitespace collapse and the plain text of an inline subtree.
- `ImageLabel.cs` — alt, else file name, else `image`; normalised last.
- `GitHubPipeline.cs` — the shared pipeline (`Configure` without the pass, `Instance` with it).
- `GitHubHtmlExtension.cs` — subscribes `DocumentProcessed`; the boundary catch.
- `GitHubHtmlPass.cs` — limits, the block walk, ordinals.
- `InlinePass.cs` — per leaf block: normalise, pair, hoist.
- `InlinePairing.cs` — the inline rule and its depth budget.
- `LinkHoister.cs` — links above their emphasis ancestors.
- `HtmlBlockPartKind.cs`, `HtmlBlockPart.cs`, `HtmlBlockPlan.cs`, `HtmlBlockReader.cs` — the block rule: validate one `HtmlBlock`, describe what it would become.
- `InlineBuildMode.cs`, `InlineBuilder.cs` — tokens to inlines for a paragraph, a summary, a `pre`.
- `HtmlPreBlock.cs`, `DetailsBlock.cs` — the two node types.
- `DetailsFolder.cs` — matching, rejection propagation, the depth check, the fold.

**`src/Capacitor.App/Views/`**
- `MarkdownFlavor.cs` — `Chat`, `GitHub`.
- `SourceText.cs` — source text as runs split at line ends.
- `LinkScope.cs` — one hyperlink at a time.
- `KcapMarkdownExtension.cs` (modify) — a mode, the scope, the image rule, the source helper.
- `HtmlPreBlockRenderer.cs`, `DetailsBlockRenderer.cs`, `DetailsState.cs`, `DetailsExtension.cs`.
- `MarkdownView.cs` (modify) — `Flavor`, details state, re-render on toggle.
- `MarkdownStyles.axaml` (modify), `PullRequestReader.axaml` (modify).

**`test/Capacitor.App.Tests.Unit/`**
- `GitHubHtml/Trees.cs`, `GitHubHtml/TreeDump.cs` — parse helpers and a compact tree printer.
- `GitHubHtml/HtmlTokenizerTests.cs`, `HtmlTagsTests.cs`, `ImageLabelTests.cs`, `NormalisationTests.cs`, `InlinePairingTests.cs`, `LinkHoistingTests.cs`, `HtmlBlockRuleTests.cs`, `DetailsFoldTests.cs`, `PassTotalityTests.cs`.
- `MarkdownViewHarness.cs` (new; the private helpers of `MarkdownViewTests` move here), `MarkdownViewTests.cs` (modify), `GitHubMarkdownViewTests.cs`, `DetailsViewTests.cs`.

**Docs** — `docs/CHANGES.md` (new entry at the top).

---

### Task 1: HTML tokenizer

**Files:**
- Create: `src/Capacitor.App/GitHubHtml/HtmlTokenKind.cs`, `HtmlToken.cs`, `HtmlTokenization.cs`, `HtmlTokenizer.cs`
- Test: `test/Capacitor.App.Tests.Unit/GitHubHtml/HtmlTokenizerTests.cs`

**Interfaces:**
- Produces: `HtmlTokenizer.Tokenize(string) : HtmlTokenization`; `HtmlTokenization(IReadOnlyList<HtmlToken> Tokens, bool Malformed)`; `HtmlToken(HtmlTokenKind Kind, string Name, string Text, IReadOnlyDictionary<string,string> Attributes)` with `Attribute(string) : string?` and `IsWhitespace`; `HtmlTokenKind { Text, OpenTag, CloseTag, Comment }`. `Name` is lower-case; attribute values are entity-decoded; `Text` of a text token is raw (undecoded).

- [ ] **Step 1: Write the failing tests**

`test/Capacitor.App.Tests.Unit/GitHubHtml/HtmlTokenizerTests.cs`:

```csharp
using Capacitor.App.GitHubHtml;

namespace Capacitor.App.Tests.Unit.GitHubHtml;

public class HtmlTokenizerTests {
    static string Shape(HtmlTokenization result) => string.Join(" ", result.Tokens.Select(t => t.Kind switch {
        HtmlTokenKind.Text    => $"text({t.Text})",
        HtmlTokenKind.Comment => "comment",
        HtmlTokenKind.OpenTag => $"<{t.Name}{string.Concat(t.Attributes.OrderBy(a => a.Key).Select(a => $" {a.Key}={a.Value}"))}>",
        _                     => $"</{t.Name}>",
    }));

    [Test]
    public async Task Tags_text_and_comments_come_out_in_order() {
        var result = HtmlTokenizer.Tokenize("a<B>x</b><!-- c -->y");
        await Assert.That(Shape(result)).IsEqualTo("text(a) <b> text(x) </b> comment text(y)");
        await Assert.That(result.Malformed).IsFalse();
    }

    /// Pins the three attribute quotings, a bare boolean attribute, entity decoding in a value,
    /// and a trailing slash leaving the tag an open tag.
    [Test]
    public async Task Attributes_read_in_every_quoting() {
        var result = HtmlTokenizer.Tokenize("<img src=\"https://h/a?x=1&amp;y=2\" alt='two words' width=20 />" + "<details open>");
        await Assert.That(Shape(result)).IsEqualTo("<img alt=two words src=https://h/a?x=1&y=2 width=20> <details open=>");
        await Assert.That(result.Tokens[0].Attribute("ALT")).IsEqualTo("two words");
        await Assert.That(result.Tokens[0].Attribute("missing")).IsNull();
    }

    [Test]
    public async Task A_tag_may_span_lines() {
        var result = HtmlTokenizer.Tokenize("<span\n title=\"x\">");
        await Assert.That(Shape(result)).IsEqualTo("<span title=x>");
    }

    [Test]
    public async Task A_lone_angle_bracket_is_text_and_not_malformed() {
        var result = HtmlTokenizer.Tokenize("a < b and 1<2");
        await Assert.That(Shape(result)).IsEqualTo("text(a < b and 1<2)");
        await Assert.That(result.Malformed).IsFalse();
    }

    /// Pins the no-throw contract: input that starts a tag or a comment and never finishes comes
    /// back as text and marks the result.
    [Test]
    [Arguments("<a href=\"")]
    [Arguments("<a")]
    [Arguments("</")]
    [Arguments("</b")]
    [Arguments("<!-- never closed")]
    [Arguments("x <b y")]
    public async Task Unfinished_input_is_text_and_malformed(string html) {
        var result = HtmlTokenizer.Tokenize(html);
        await Assert.That(result.Tokens.All(t => t.Kind == HtmlTokenKind.Text)).IsTrue();
        await Assert.That(string.Concat(result.Tokens.Select(t => t.Text))).IsEqualTo(html);
        await Assert.That(result.Malformed).IsTrue();
    }

    [Test]
    public async Task A_bare_angle_bracket_at_the_end_is_plain_text() {
        var result = HtmlTokenizer.Tokenize("x <");
        await Assert.That(Shape(result)).IsEqualTo("text(x <)");
        await Assert.That(result.Malformed).IsFalse();
    }

    [Test]
    public async Task Whitespace_only_text_is_recognised() {
        var result = HtmlTokenizer.Tokenize("<b> \n </b>");
        await Assert.That(result.Tokens[1].IsWhitespace).IsTrue();
        await Assert.That(result.Tokens[0].IsWhitespace).IsFalse();
    }

    /// Pins linear work: every one of these tag starts scans a quoted value that closes only at the
    /// very end and then fails on the `!`, which costs the square of the input unless the quote
    /// search is remembered — minutes at this size, against milliseconds.
    [Test]
    [Timeout(10_000)]
    public async Task Repeated_unclosed_quotes_stay_linear(CancellationToken _) {
        var html = string.Concat(Enumerable.Repeat("<a x=\"", 200_000)) + "\"!>";
        var result = HtmlTokenizer.Tokenize(html);
        await Assert.That(result.Malformed).IsTrue();
        await Assert.That(result.Tokens.All(t => t.Kind == HtmlTokenKind.Text)).IsTrue();
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/HtmlTokenizerTests/*"`
Expected: build FAILS with `CS0246: The type or namespace name 'GitHubHtml' does not exist`.

- [ ] **Step 3: Write the types**

`src/Capacitor.App/GitHubHtml/HtmlTokenKind.cs`:

```csharp
namespace Capacitor.App.GitHubHtml;

public enum HtmlTokenKind { Text, OpenTag, CloseTag, Comment }
```

`src/Capacitor.App/GitHubHtml/HtmlToken.cs`:

```csharp
using System.Collections.Frozen;

namespace Capacitor.App.GitHubHtml;

/// One piece of an HTML fragment. `Name` is lower-case, attribute values are entity-decoded, and
/// `Text` is the raw source of the piece.
public sealed record HtmlToken(HtmlTokenKind Kind, string Name, string Text, IReadOnlyDictionary<string, string> Attributes) {
    public static HtmlToken OfText(string text) => new(HtmlTokenKind.Text, "", text, FrozenDictionary<string, string>.Empty);

    public static HtmlToken OfComment(string text) => new(HtmlTokenKind.Comment, "", text, FrozenDictionary<string, string>.Empty);

    public string? Attribute(string name) => Attributes.TryGetValue(name, out var value) ? value : null;

    public bool IsWhitespace => Kind == HtmlTokenKind.Text && string.IsNullOrWhiteSpace(Text);
}
```

`src/Capacitor.App/GitHubHtml/HtmlTokenization.cs`:

```csharp
namespace Capacitor.App.GitHubHtml;

/// `Malformed` means the input started a tag or a comment it never finished.
public sealed record HtmlTokenization(IReadOnlyList<HtmlToken> Tokens, bool Malformed);
```

`src/Capacitor.App/GitHubHtml/HtmlTokenizer.cs`:

```csharp
using System.Collections.Frozen;
using System.Net;

namespace Capacitor.App.GitHubHtml;

/// Splits an HTML fragment into tags, text and comments in one forward scan. It never throws:
/// whatever does not finish as a tag or a comment is text, and marks the result malformed.
public static class HtmlTokenizer {
    public static HtmlTokenization Tokenize(string html) => new Scanner(html).Run();

    sealed class Scanner(string html) {
        readonly List<HtmlToken> _tokens = [];
        readonly int _lastClose = html.LastIndexOf('>');
        // A search for a closing quote answers every later start up to where it landed, and a
        // failed one stays failed, so repeated unclosed quotes cost one scan rather than one each.
        int _doubleFrom = -1, _doubleAt = -1, _singleFrom = -1, _singleAt = -1;
        bool _malformed;

        public HtmlTokenization Run() {
            var i = 0;
            var textStart = 0;
            while (i < html.Length) {
                if (html[i] != '<') { i++; continue; }

                if (string.CompareOrdinal(html, i, "<!--", 0, 4) == 0) {
                    var end = html.IndexOf("-->", i + 4, StringComparison.Ordinal);
                    if (end < 0) { _malformed = true; break; }
                    Text(textStart, i);
                    _tokens.Add(HtmlToken.OfComment(html[i..(end + 3)]));
                    i = textStart = end + 3;
                    continue;
                }

                var next = i + 1 < html.Length ? html[i + 1] : '\0';
                if (!char.IsAsciiLetter(next) && next != '/') { i++; continue; }

                if (i < _lastClose && TryReadTag(i, out var tag, out var after)) {
                    Text(textStart, i);
                    _tokens.Add(tag);
                    i = textStart = after;
                    continue;
                }
                _malformed = true;
                i++;
            }
            Text(textStart, html.Length);
            return new(_tokens, _malformed);
        }

        void Text(int start, int end) {
            if (end > start) _tokens.Add(HtmlToken.OfText(html[start..end]));
        }

        bool TryReadTag(int start, out HtmlToken token, out int after) {
            token = null!;
            after = start;
            var i = start + 1;
            var close = html[i] == '/';
            if (close) i++;

            var nameStart = i;
            if (i >= html.Length || !char.IsAsciiLetter(html[i])) return false;
            while (i < html.Length && (char.IsAsciiLetterOrDigit(html[i]) || html[i] == '-')) i++;
            var name = html[nameStart..i].ToLowerInvariant();

            Dictionary<string, string>? attributes = null;
            while (true) {
                var gap = i;
                while (i < html.Length && char.IsWhiteSpace(html[i])) i++;
                if (i >= html.Length) return false;
                if (html[i] == '>') { i++; break; }
                if (html[i] == '/') {
                    if (i + 1 < html.Length && html[i + 1] == '>') { i += 2; break; }
                    return false;
                }
                if (close || i == gap) return false;

                var attributeStart = i;
                while (i < html.Length && IsAttributeNameChar(html[i])) i++;
                if (i == attributeStart) return false;
                var attribute = html[attributeStart..i];

                var value = "";
                var afterName = i;
                while (i < html.Length && char.IsWhiteSpace(html[i])) i++;
                if (i < html.Length && html[i] == '=') {
                    i++;
                    while (i < html.Length && char.IsWhiteSpace(html[i])) i++;
                    if (i >= html.Length) return false;
                    if (html[i] is '"' or '\'') {
                        var end = NextQuote(html[i], i + 1);
                        if (end < 0) return false;
                        value = html[(i + 1)..end];
                        i = end + 1;
                    } else {
                        var valueStart = i;
                        while (i < html.Length && !char.IsWhiteSpace(html[i]) && html[i] is not ('"' or '\'' or '=' or '<' or '>' or '`')) i++;
                        if (i == valueStart) return false;
                        value = html[valueStart..i];
                    }
                } else {
                    i = afterName;
                }
                (attributes ??= new(StringComparer.OrdinalIgnoreCase)).TryAdd(attribute, WebUtility.HtmlDecode(value));
            }

            token = new(close ? HtmlTokenKind.CloseTag : HtmlTokenKind.OpenTag, name, html[start..i],
                attributes is null ? FrozenDictionary<string, string>.Empty : attributes);
            after = i;
            return true;
        }

        int NextQuote(char quote, int from) {
            ref var cachedFrom = ref quote == '"' ? ref _doubleFrom : ref _singleFrom;
            ref var cachedAt = ref quote == '"' ? ref _doubleAt : ref _singleAt;
            if (cachedFrom >= 0 && from >= cachedFrom && (cachedAt < 0 || from <= cachedAt)) return cachedAt;
            cachedFrom = from;
            cachedAt = html.IndexOf(quote, from);
            return cachedAt;
        }

        static bool IsAttributeNameChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '_' or ':' or '.' or '-';
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/HtmlTokenizerTests/*"`
Expected: PASS, 13 tests.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.App/GitHubHtml/HtmlTokenKind.cs src/Capacitor.App/GitHubHtml/HtmlToken.cs src/Capacitor.App/GitHubHtml/HtmlTokenization.cs src/Capacitor.App/GitHubHtml/HtmlTokenizer.cs test/Capacitor.App.Tests.Unit/GitHubHtml/HtmlTokenizerTests.cs
git commit -m "Tokenize HTML fragments for the pull request reader (#982)" -m "One forward scan that never throws: comment bodies are written by strangers." -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 2: Tag classes, plain text, image labels

**Files:**
- Create: `src/Capacitor.App/GitHubHtml/HtmlTagClass.cs`, `HtmlTags.cs`, `InlineText.cs`, `ImageLabel.cs`
- Test: `test/Capacitor.App.Tests.Unit/GitHubHtml/HtmlTagsTests.cs`, `ImageLabelTests.cs`

**Interfaces:**
- Produces:
  - `HtmlTagClass { Unknown, Void, Structural, Paired }`
  - `HtmlTags.Classify(string name) : HtmlTagClass`; `HtmlTags.IsInline(string) : bool` (paired tags legal inside a paragraph); `HtmlTags.IsCode(string) : bool`; `HtmlTags.TryEmphasis(string, out char delimiter, out int count) : bool`
  - `InlineText.Collapse(string?) : string` (every whitespace run to one space, no trim); `InlineText.Plain(ContainerInline) : string`; `InlineText.Plain(Inline first, Inline? until) : string` (siblings from `first` up to but excluding `until`)
  - `ImageLabel.For(string? alt, string? url) : string`

- [ ] **Step 1: Write the failing tests**

`test/Capacitor.App.Tests.Unit/GitHubHtml/HtmlTagsTests.cs`:

```csharp
using Capacitor.App.GitHubHtml;

namespace Capacitor.App.Tests.Unit.GitHubHtml;

public class HtmlTagsTests {
    [Test]
    [Arguments("img", HtmlTagClass.Void)]
    [Arguments("br", HtmlTagClass.Void)]
    [Arguments("details", HtmlTagClass.Structural)]
    [Arguments("summary", HtmlTagClass.Paired)]
    [Arguments("pre", HtmlTagClass.Paired)]
    [Arguments("a", HtmlTagClass.Paired)]
    [Arguments("kbd", HtmlTagClass.Paired)]
    [Arguments("strike", HtmlTagClass.Paired)]
    [Arguments("div", HtmlTagClass.Unknown)]
    [Arguments("table", HtmlTagClass.Unknown)]
    [Arguments("script", HtmlTagClass.Unknown)]
    public async Task Tags_fall_into_their_class(string name, HtmlTagClass expected) =>
        await Assert.That(HtmlTags.Classify(name)).IsEqualTo(expected);

    [Test]
    [Arguments("b", '*', 2)]
    [Arguments("strong", '*', 2)]
    [Arguments("i", '*', 1)]
    [Arguments("em", '*', 1)]
    [Arguments("del", '~', 2)]
    [Arguments("s", '~', 2)]
    [Arguments("strike", '~', 2)]
    [Arguments("ins", '+', 2)]
    [Arguments("sub", '~', 1)]
    [Arguments("sup", '^', 1)]
    public async Task Formatting_tags_map_to_emphasis_delimiters(string name, char delimiter, int count) {
        await Assert.That(HtmlTags.TryEmphasis(name, out var actualDelimiter, out var actualCount)).IsTrue();
        await Assert.That(actualDelimiter).IsEqualTo(delimiter);
        await Assert.That(actualCount).IsEqualTo(count);
    }

    /// Pins which paired tags a paragraph may hold: `summary` and `pre` pair but are block-only.
    [Test]
    public async Task Block_only_tags_are_not_inline() {
        await Assert.That(HtmlTags.IsInline("summary")).IsFalse();
        await Assert.That(HtmlTags.IsInline("pre")).IsFalse();
        await Assert.That(HtmlTags.IsInline("a")).IsTrue();
        await Assert.That(HtmlTags.IsInline("code")).IsTrue();
        await Assert.That(HtmlTags.IsCode("samp")).IsTrue();
        await Assert.That(HtmlTags.IsCode("b")).IsFalse();
    }

    [Test]
    public async Task Whitespace_runs_collapse_to_one_space() {
        await Assert.That(InlineText.Collapse(" a \r\n\t b  ")).IsEqualTo(" a b ");
        await Assert.That(InlineText.Collapse(null)).IsEqualTo("");
    }
}
```

`test/Capacitor.App.Tests.Unit/GitHubHtml/ImageLabelTests.cs`:

```csharp
using Capacitor.App.GitHubHtml;

namespace Capacitor.App.Tests.Unit.GitHubHtml;

public class ImageLabelTests {
    [Test]
    [Arguments("Remediation recommended", "https://img.shields.io/badge/Medium-634FD1", "Remediation recommended")]
    [Arguments("  two\n words ", "https://h/x.png", "two words")]
    [Arguments("", "https://h/path/name.png?x=1#f", "name.png")]
    [Arguments(null, "https://h/path/name.png", "name.png")]
    [Arguments("   ", "https://h/dir/", "dir")]
    [Arguments("", "relative/shot.png", "shot.png")]
    [Arguments("", "", "image")]
    [Arguments(null, null, "image")]
    [Arguments("", "https://h/", "image")]
    public async Task The_label_is_the_alt_then_the_file_name_then_a_word(string? alt, string? url, string expected) =>
        await Assert.That(ImageLabel.For(alt, url)).IsEqualTo(expected);

    /// Pins normalisation coming last: unescaping a file name can produce a line end, and a line
    /// end inside a run hangs layout.
    [Test]
    [Arguments("https://h/a%0Ab.png", "a b.png")]
    [Arguments("https://h/a%0D%0Ab.png", "a b.png")]
    [Arguments("https://h/%20%0A%20", "image")]
    public async Task An_unescaped_file_name_never_carries_a_line_end(string url, string expected) {
        var label = ImageLabel.For("", url);
        await Assert.That(label).IsEqualTo(expected);
        await Assert.That(label.Contains('\r') || label.Contains('\n')).IsFalse();
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/HtmlTagsTests/*"`
Expected: build FAILS with `CS0103: The name 'HtmlTags' does not exist`.

- [ ] **Step 3: Write the types**

`src/Capacitor.App/GitHubHtml/HtmlTagClass.cs`:

```csharp
namespace Capacitor.App.GitHubHtml;

/// Void tags convert on their own; the structural tag pairs across blocks; paired tags must
/// balance where they stand.
public enum HtmlTagClass { Unknown, Void, Structural, Paired }
```

`src/Capacitor.App/GitHubHtml/HtmlTags.cs`:

```csharp
namespace Capacitor.App.GitHubHtml;

/// The HTML the reader converts. Names are lower-case, as the tokenizer delivers them.
public static class HtmlTags {
    public static HtmlTagClass Classify(string name) => name switch {
        "img" or "br"          => HtmlTagClass.Void,
        "details"              => HtmlTagClass.Structural,
        "summary" or "pre"     => HtmlTagClass.Paired,
        _ when IsInline(name)  => HtmlTagClass.Paired,
        _                      => HtmlTagClass.Unknown,
    };

    /// Paired tags a paragraph may hold; `summary` and `pre` pair too, but only at block level.
    public static bool IsInline(string name) => name == "a" || IsCode(name) || TryEmphasis(name, out _, out _);

    public static bool IsCode(string name) => name is "code" or "kbd" or "tt" or "samp";

    public static bool TryEmphasis(string name, out char delimiter, out int count) {
        (delimiter, count) = name switch {
            "b" or "strong"            => ('*', 2),
            "i" or "em"                => ('*', 1),
            "del" or "s" or "strike"   => ('~', 2),
            "ins"                      => ('+', 2),
            "sub"                      => ('~', 1),
            "sup"                      => ('^', 1),
            _                          => ('\0', 0),
        };
        return count > 0;
    }
}
```

`src/Capacitor.App/GitHubHtml/InlineText.cs`:

```csharp
using System.Text;
using Markdig.Syntax.Inlines;

namespace Capacitor.App.GitHubHtml;

public static class InlineText {
    public static string Collapse(string? text) {
        if (string.IsNullOrEmpty(text)) return "";
        var builder = new StringBuilder(text.Length);
        var inWhitespace = false;
        foreach (var c in text) {
            if (char.IsWhiteSpace(c)) {
                if (!inWhitespace) builder.Append(' ');
                inWhitespace = true;
            } else {
                builder.Append(c);
                inWhitespace = false;
            }
        }
        return builder.ToString();
    }

    public static string Plain(ContainerInline container) =>
        container.FirstChild is { } first ? Plain(first, null) : "";

    /// The text of `first` and its following siblings, stopping before `until`. A line break reads
    /// as a space and a tag left as source reads as its source.
    public static string Plain(Inline first, Inline? until) {
        var builder = new StringBuilder();
        for (var inline = first; inline is not null && !ReferenceEquals(inline, until); inline = inline.NextSibling)
            Append(builder, inline);
        return builder.ToString();
    }

    static void Append(StringBuilder builder, Inline inline) {
        switch (inline) {
            case LiteralInline literal:    builder.Append(literal.Content.ToString()); break;
            case CodeInline code:          builder.Append(code.Content); break;
            case HtmlEntityInline entity:  builder.Append(entity.Transcoded.ToString()); break;
            case LineBreakInline:          builder.Append(' '); break;
            case HtmlInline html:          builder.Append(html.Tag); break;
            case AutolinkInline auto:      builder.Append(auto.Url); break;
            case ContainerInline nested:
                for (var child = nested.FirstChild; child is not null; child = child.NextSibling) Append(builder, child);
                break;
        }
    }
}
```

`Append` recurses by inline nesting, which the parser's own limit and `MaxDepth` bound.

`src/Capacitor.App/GitHubHtml/ImageLabel.cs`:

```csharp
namespace Capacitor.App.GitHubHtml;

/// What an image reads as: its alt text, else its file name, else a word. The choice is
/// normalised after it is made, because unescaping a file name can produce a line end.
public static class ImageLabel {
    public static string For(string? alt, string? url) {
        string?[] candidates = [alt, FileName(url)];
        foreach (var candidate in candidates) {
            var label = InlineText.Collapse(candidate).Trim();
            if (label.Length > 0) return label;
        }
        return "image";
    }

    static string? FileName(string? url) {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var path = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : url.Split('?', '#')[0];
        path = path.TrimEnd('/');
        return Uri.UnescapeDataString(path[(path.LastIndexOf('/') + 1)..]);
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/HtmlTagsTests/*"` and then the same with `ImageLabelTests`.
Expected: PASS (23 and 12 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.App/GitHubHtml/HtmlTagClass.cs src/Capacitor.App/GitHubHtml/HtmlTags.cs src/Capacitor.App/GitHubHtml/InlineText.cs src/Capacitor.App/GitHubHtml/ImageLabel.cs test/Capacitor.App.Tests.Unit/GitHubHtml/HtmlTagsTests.cs test/Capacitor.App.Tests.Unit/GitHubHtml/ImageLabelTests.cs
git commit -m "Classify the HTML tags the reader converts and label images (#982)" -m "An image label is normalised after it is chosen: an unescaped file name can carry a line end." -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 3: The pass scaffold and normalisation

The pipeline, the extension with its boundary catch, the block walk, and the first rule: before anything else touches a leaf block's inlines, every image carries its label as a literal child and every non-email CommonMark autolink is a `LinkInline`.

**Files:**
- Create: `src/Capacitor.App/GitHubHtml/GitHubPipeline.cs`, `GitHubHtmlExtension.cs`, `GitHubHtmlPass.cs`, `InlinePass.cs`, `InlinePairing.cs`
- Create: `test/Capacitor.App.Tests.Unit/GitHubHtml/Trees.cs`, `TreeDump.cs`, `NormalisationTests.cs`

**Interfaces:**
- Consumes: `ImageLabel.For`, `InlineText.Plain`.
- Produces:
  - `GitHubPipeline.Configure(MarkdownPipelineBuilder) : MarkdownPipelineBuilder` (supported extensions + alerts, no pass); `GitHubPipeline.Instance : MarkdownPipeline` (with the pass).
  - `GitHubHtmlPass.MaxDepth = 100`, `MaxDetailsNesting = 8`, `MaxHoistedEmphasis = 8`; `GitHubHtmlPass.Run(MarkdownDocument)`.
  - `InlinePass.Process(LeafBlock leaf, int leafDepth) : int` — returns how many levels the leaf's inlines occupy below it (0 when it has none).
  - `InlinePairing.Process(ContainerInline container, int containerDepth) : int` — returns how many levels the container's children occupy below it. In this task it only measures; Task 4 gives it the inline rule.
  - Test helpers `Trees.Parse(string) : MarkdownDocument` (with the pass), `Trees.ParseWithoutPass(string)`, `Trees.Dump(string) : string`, `Trees.MaxDepth(MarkdownDocument) : int`, `TreeDump.Of(MarkdownObject) : string`.

**Depth convention, used by every later task:** the document is at depth 0 and a node's depth is the number of containers above it. A paragraph directly in the document is at 1, its root inline container at 2, a literal in it at 3. "Reach" is the number of levels a subtree occupies counting its top node: a literal has reach 1, `em('x')` reach 2. A node at depth `d` with reach `r` has its deepest node at `d + r - 1`.

- [ ] **Step 1: Write the test helpers**

`test/Capacitor.App.Tests.Unit/GitHubHtml/TreeDump.cs`:

```csharp
using System.Text;
using Capacitor.App.GitHubHtml;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Capacitor.App.Tests.Unit.GitHubHtml;

/// Prints a Markdig tree on one line, so a test can assert an exact tree:
/// `doc(p('a ',em*2('b')))`.
static class TreeDump {
    public static string Of(MarkdownObject node) {
        var builder = new StringBuilder();
        Write(builder, node);
        return builder.ToString();
    }

    static void Write(StringBuilder builder, MarkdownObject node) {
        switch (node) {
            case MarkdownDocument document: Blocks(builder, "doc", document); break;
            case DetailsBlock details:
                builder.Append(details.IsOpen ? "details+" : "details").Append('#').Append(details.Ordinal).Append('{');
                Inlines(builder, details.Summary);
                builder.Append('}');
                Blocks(builder, "", details);
                break;
            case HtmlPreBlock pre:        Leaf(builder, "pre", pre); break;
            case ParagraphBlock paragraph: Leaf(builder, "p", paragraph); break;
            case HeadingBlock heading:    Leaf(builder, "h" + heading.Level, heading); break;
            case HtmlBlock html:          builder.Append("html(").Append(Quote(Lines(html))).Append(')'); break;
            case QuoteBlock quote:        Blocks(builder, "quote", quote); break;
            case ListBlock list:          Blocks(builder, "list", list); break;
            case ListItemBlock item:      Blocks(builder, "li", item); break;
            case ContainerBlock container: Blocks(builder, container.GetType().Name, container); break;
            case LeafBlock leaf:          Leaf(builder, leaf.GetType().Name, leaf); break;
            case LiteralInline literal:   builder.Append(Quote(literal.Content.ToString())); break;
            case CodeInline code:         builder.Append("code(").Append(Quote(code.Content)).Append(')'); break;
            case LineBreakInline line:    builder.Append(line.IsHard ? "br" : "sp"); break;
            case HtmlInline tag:          builder.Append("tag(").Append(Quote(tag.Tag)).Append(')'); break;
            case AutolinkInline auto:     builder.Append("auto(").Append(auto.Url).Append(')'); break;
            case HtmlEntityInline entity: builder.Append(Quote(entity.Transcoded.ToString())); break;
            case EmphasisInline emphasis:
                builder.Append("em").Append(emphasis.DelimiterChar).Append(emphasis.DelimiterCount).Append('(');
                Inlines(builder, emphasis);
                builder.Append(')');
                break;
            case LinkInline link:
                builder.Append(link.IsImage ? "img[" : "link[").Append(link.Url).Append("](");
                Inlines(builder, link);
                builder.Append(')');
                break;
            case ContainerInline other:
                builder.Append(other.GetType().Name).Append('(');
                Inlines(builder, other);
                builder.Append(')');
                break;
            default: builder.Append(node.GetType().Name); break;
        }
    }

    static void Blocks(StringBuilder builder, string name, ContainerBlock container) {
        builder.Append(name).Append('(');
        for (var i = 0; i < container.Count; i++) {
            if (i > 0) builder.Append(',');
            Write(builder, container[i]);
        }
        builder.Append(')');
    }

    static void Leaf(StringBuilder builder, string name, LeafBlock leaf) {
        builder.Append(name).Append('(');
        if (leaf.Inline is not null) Inlines(builder, leaf.Inline);
        builder.Append(')');
    }

    static void Inlines(StringBuilder builder, ContainerInline container) {
        var first = true;
        foreach (var inline in container) {
            if (!first) builder.Append(',');
            first = false;
            Write(builder, inline);
        }
    }

    static string Lines(LeafBlock block) {
        var lines = new List<string>();
        for (var i = 0; i < block.Lines.Count; i++) lines.Add(block.Lines.Lines[i].Slice.ToString());
        return string.Join("\\n", lines);
    }

    static string Quote(string text) => "'" + text.Replace("\n", "\\n").Replace("\r", "\\r") + "'";
}
```

`DetailsBlock` and `HtmlPreBlock` do not exist until Tasks 6 and 7. For this task, leave those two `case` lines out and add them in the task that creates each type — each of those tasks says so.

`test/Capacitor.App.Tests.Unit/GitHubHtml/Trees.cs`:

```csharp
using Capacitor.App.GitHubHtml;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Capacitor.App.Tests.Unit.GitHubHtml;

static class Trees {
    static readonly MarkdownPipeline WithoutPass = GitHubPipeline.Configure(new MarkdownPipelineBuilder()).Build();

    public static MarkdownDocument Parse(string markdown) => Markdown.Parse(markdown, GitHubPipeline.Instance);

    /// What Markdig alone makes of the source: a block fixture asserts this first, because whether
    /// a line opens an HTML block or a paragraph decides which rule the fixture exercises.
    public static MarkdownDocument ParseWithoutPass(string markdown) => Markdown.Parse(markdown, WithoutPass);

    public static string Dump(string markdown) => TreeDump.Of(Parse(markdown));

    public static string Shape(string markdown) => TreeDump.Of(ParseWithoutPass(markdown));

    /// The depth of the deepest node, the document at 0, walked without recursion.
    public static int MaxDepth(MarkdownDocument document) {
        var deepest = 0;
        var pending = new Stack<(MarkdownObject Node, int Depth)>();
        pending.Push((document, 0));
        while (pending.Count > 0) {
            var (node, depth) = pending.Pop();
            deepest = Math.Max(deepest, depth);
            switch (node) {
                case ContainerBlock blocks:
                    foreach (var child in blocks) pending.Push((child, depth + 1));
                    break;
                case LeafBlock { Inline: { } inlines }:
                    pending.Push((inlines, depth + 1));
                    break;
                case ContainerInline container:
                    foreach (var child in container) pending.Push((child, depth + 1));
                    break;
            }
        }
        return deepest;
    }
}
```

- [ ] **Step 2: Write the failing tests**

`test/Capacitor.App.Tests.Unit/GitHubHtml/NormalisationTests.cs`:

```csharp
using Capacitor.App.GitHubHtml;

namespace Capacitor.App.Tests.Unit.GitHubHtml;

public class NormalisationTests {
    [Test]
    public async Task A_markdown_image_holds_its_label_as_one_literal() {
        await Assert.That(Trees.Dump("![two *bold* words](https://h/x.png)")).IsEqualTo("doc(p(img[https://h/x.png]('two bold words')))");
        await Assert.That(Trees.Dump("![](https://h/path/name.png)")).IsEqualTo("doc(p(img[https://h/path/name.png]('name.png')))");
    }

    [Test]
    public async Task A_commonmark_autolink_becomes_a_link_and_an_email_stays() {
        await Assert.That(Trees.Dump("<https://example.com>")).IsEqualTo("doc(p(link[https://example.com]('https://example.com')))");
        await Assert.That(Trees.Dump("<a@b.example>")).IsEqualTo("doc(p(auto(a@b.example)))");
    }

    [Test]
    public async Task A_bare_url_already_is_a_link() =>
        await Assert.That(Trees.Dump("see https://example.com/x now")).IsEqualTo("doc(p('see ',link[https://example.com/x]('https://example.com/x'),' now'))");

    /// Pins the refusal: nested block quotes put the paragraph's inlines at the depth limit in the
    /// parsed tree, where giving a leaf a child would pass it. One quote fewer and both normalise.
    [Test]
    public async Task Normalisation_is_refused_where_its_literal_would_pass_the_depth_limit() {
        var refused = Trees.Parse(Quoted(97, "<https://example.com> ![](https://h/name.png)"));
        await Assert.That(TreeDump.Of(refused)).Contains("auto(https://example.com)");
        await Assert.That(TreeDump.Of(refused)).Contains("img[https://h/name.png]()");
        await Assert.That(Trees.MaxDepth(refused)).IsEqualTo(GitHubHtmlPass.MaxDepth);

        var accepted = Trees.Parse(Quoted(96, "<https://example.com> ![](https://h/name.png)"));
        await Assert.That(TreeDump.Of(accepted)).Contains("link[https://example.com]('https://example.com')");
        await Assert.That(TreeDump.Of(accepted)).Contains("img[https://h/name.png]('name.png')");
        await Assert.That(Trees.MaxDepth(accepted)).IsEqualTo(GitHubHtmlPass.MaxDepth);
    }

    /// An image that already has children can always take its label: replacing them never deepens.
    [Test]
    public async Task An_image_with_alt_text_is_labelled_at_any_depth() =>
        await Assert.That(TreeDump.Of(Trees.Parse(Quoted(97, "![alt](https://h/name.png)")))).Contains("img[https://h/name.png]('alt')");

    [Test]
    public async Task The_pipeline_parses_alert_blocks() =>
        await Assert.That(Trees.Dump("> [!NOTE]\n> text")).StartsWith("doc(quote(");

    internal static string Quoted(int levels, string text) => string.Concat(Enumerable.Repeat("> ", levels)) + text;
}
```

With `n` quotes the paragraph is at depth `n + 1`, its root container at `n + 2`, the autolink at `n + 3`, and its literal would be at `n + 4`: 97 quotes put that at 101.

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/NormalisationTests/*"`
Expected: build FAILS with `CS0103: The name 'GitHubPipeline' does not exist`.

- [ ] **Step 4: Write the pass scaffold**

`src/Capacitor.App/GitHubHtml/GitHubPipeline.cs`:

```csharp
using Markdig;
using Markdig.Extensions.Alerts;
using MarkView.Avalonia;

namespace Capacitor.App.GitHubHtml;

public static class GitHubPipeline {
    /// One pipeline for every reader view: a pipeline is immutable once built.
    public static MarkdownPipeline Instance { get; } = Configure(new MarkdownPipelineBuilder()).Use(new GitHubHtmlExtension()).Build();

    /// What GitHub-flavoured markdown parses as before the HTML pass runs.
    public static MarkdownPipelineBuilder Configure(MarkdownPipelineBuilder builder) =>
        builder.UseSupportedExtensions().Use<AlertExtension>();
}
```

`UseSupportedExtensions` is MarkView's extension method (`MarkView.Avalonia.MarkdownExtensions`). `Use<AlertExtension>()` is spelled out rather than `UseAlertBlocks()` because MarkView and Markdig both declare that name.

`src/Capacitor.App/GitHubHtml/GitHubHtmlExtension.cs`:

```csharp
using Markdig;
using Markdig.Renderers;
using Markdig.Syntax;

namespace Capacitor.App.GitHubHtml;

public sealed class GitHubHtmlExtension : IMarkdownExtension {
    public void Setup(MarkdownPipelineBuilder pipeline) => pipeline.DocumentProcessed += Process;

    public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer) { }

    /// The pass runs on the UI thread over text a stranger wrote. A defect in it must not take the
    /// window down: whatever it had not converted renders as source.
    static void Process(MarkdownDocument document) {
        try { GitHubHtmlPass.Run(document); }
        catch (Exception ex) { Console.Error.WriteLine($"kcap: html pass failed: {ex.Message}"); }
    }
}
```

`src/Capacitor.App/GitHubHtml/GitHubHtmlPass.cs`:

```csharp
using Markdig.Syntax;

namespace Capacitor.App.GitHubHtml;

/// Rewrites the HTML the reader understands into Markdig nodes. Depth counts the containers above
/// a node, the document at 0; a subtree's reach counts the levels it occupies, its top included.
public static class GitHubHtmlPass {
    /// Markdig's renderer throws past 128 nested containers, after parsing has returned.
    public const int MaxDepth = 100;
    public const int MaxDetailsNesting = 8;
    public const int MaxHoistedEmphasis = 8;

    public static void Run(MarkdownDocument document) => ProcessContainer(document, 0);

    /// Returns the reach of the container's children.
    static int ProcessContainer(ContainerBlock container, int depth) {
        var reach = 0;
        foreach (var child in container) {
            reach = Math.Max(reach, child switch {
                ContainerBlock nested => 1 + ProcessContainer(nested, depth + 1),
                LeafBlock leaf        => 1 + InlinePass.Process(leaf, depth + 1),
                _                     => 1,
            });
        }
        return reach;
    }
}
```

`src/Capacitor.App/GitHubHtml/InlinePass.cs`:

```csharp
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Capacitor.App.GitHubHtml;

/// One leaf block's inline tree, in the order that keeps every measured height true:
/// normalisation adds its children before the inline rule budgets around them.
static class InlinePass {
    /// Returns the reach of the leaf's inlines, 0 when it has none.
    public static int Process(LeafBlock leaf, int leafDepth) {
        if (leaf.Inline is not { } root) return 0;
        var rootDepth = leafDepth + 1;
        Normalise(root, rootDepth);
        return 1 + InlinePairing.Process(root, rootDepth);
    }

    static void Normalise(ContainerInline root, int rootDepth) {
        var pending = new Stack<(ContainerInline Container, int Depth)>();
        pending.Push((root, rootDepth));
        while (pending.Count > 0) {
            var (container, depth) = pending.Pop();
            for (var child = container.FirstChild; child is not null;) {
                var next = child.NextSibling;
                switch (child) {
                    case LinkInline { IsImage: true } image:
                        LabelImage(image, depth + 1);
                        break;
                    case AutolinkInline { IsEmail: false } auto when depth + 2 <= GitHubHtmlPass.MaxDepth: {
                        var link = new LinkInline(auto.Url, "") { IsAutoLink = true, IsClosed = true };
                        link.AppendChild(new LiteralInline(auto.Url));
                        auto.ReplaceBy(link, copyChildren: false);
                        break;
                    }
                    case ContainerInline nested:
                        pending.Push((nested, depth + 1));
                        break;
                }
                child = next;
            }
        }
    }

    /// Replacing existing children with one literal cannot deepen the tree; giving a childless
    /// image a child can, and such an image stays childless for its renderer to label.
    static void LabelImage(LinkInline image, int imageDepth) {
        if (image.FirstChild is null && imageDepth + 1 > GitHubHtmlPass.MaxDepth) return;
        var label = ImageLabel.For(InlineText.Plain(image), image.Url);
        image.Clear();
        image.AppendChild(new LiteralInline(label));
    }
}
```

`src/Capacitor.App/GitHubHtml/InlinePairing.cs` (measuring only; Task 4 adds the inline rule):

```csharp
using Markdig.Syntax.Inlines;

namespace Capacitor.App.GitHubHtml;

static class InlinePairing {
    /// Returns the reach of the container's children.
    public static int Process(ContainerInline container, int containerDepth) {
        var reach = 0;
        for (var child = container.FirstChild; child is not null; child = child.NextSibling)
            reach = Math.Max(reach, child is ContainerInline nested ? 1 + Process(nested, containerDepth + 1) : 1);
        return reach;
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/NormalisationTests/*"`
Expected: PASS, 6 tests. If the alert test fails because `AlertBlock` prints under another name, print `Trees.Dump("> [!NOTE]\n> text")` and assert what Markdig produces — `AlertBlock` derives from `QuoteBlock`, so `quote(` is the expected opening.

- [ ] **Step 6: Commit**

```bash
git add src/Capacitor.App/GitHubHtml/GitHubPipeline.cs src/Capacitor.App/GitHubHtml/GitHubHtmlExtension.cs src/Capacitor.App/GitHubHtml/GitHubHtmlPass.cs src/Capacitor.App/GitHubHtml/InlinePass.cs src/Capacitor.App/GitHubHtml/InlinePairing.cs test/Capacitor.App.Tests.Unit/GitHubHtml/Trees.cs test/Capacitor.App.Tests.Unit/GitHubHtml/TreeDump.cs test/Capacitor.App.Tests.Unit/GitHubHtml/NormalisationTests.cs
git commit -m "Add the GitHub markdown pipeline and normalise images and autolinks (#982)" -m "Normalisation runs before anything measures a height: it gives leaves a child, and Markdig's renderer throws past 128 nested containers." -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 4: The inline rule

Within one inline container, a close tag pairs with the nearest open tag of its name; the pair and what lies between become the mapped node. Open tags above the match are discarded for good, so no synthesized node crosses another. Every wrap is budgeted against `MaxDepth` before anything moves.

**Files:**
- Modify: `src/Capacitor.App/GitHubHtml/InlinePairing.cs` (replace the whole file)
- Test: `test/Capacitor.App.Tests.Unit/GitHubHtml/InlinePairingTests.cs`

**Interfaces:**
- Consumes: `HtmlTokenizer.Tokenize`, `HtmlTags.Classify` / `IsInline` / `IsCode` / `TryEmphasis`, `InlineText.Plain(Inline, Inline?)` / `Collapse`, `ImageLabel.For`, `GitHubHtmlPass.MaxDepth`.
- Produces: `InlinePairing.Process(ContainerInline container, int containerDepth) : int` — unchanged signature; it now converts as well as measures.

- [ ] **Step 1: Write the failing tests**

`test/Capacitor.App.Tests.Unit/GitHubHtml/InlinePairingTests.cs`:

```csharp
using Capacitor.App.GitHubHtml;

namespace Capacitor.App.Tests.Unit.GitHubHtml;

public class InlinePairingTests {
    static int Count(string text, string piece) => text.Split(piece).Length - 1;

    [Test]
    [Arguments("b", "em*2")]
    [Arguments("strong", "em*2")]
    [Arguments("i", "em*1")]
    [Arguments("em", "em*1")]
    [Arguments("del", "em~2")]
    [Arguments("s", "em~2")]
    [Arguments("strike", "em~2")]
    [Arguments("ins", "em+2")]
    [Arguments("sub", "em~1")]
    [Arguments("sup", "em^1")]
    public async Task A_formatting_pair_becomes_emphasis(string tag, string emphasis) =>
        await Assert.That(Trees.Dump($"p <{tag}>x</{tag}> q")).IsEqualTo($"doc(p('p ',{emphasis}('x'),' q'))");

    [Test]
    public async Task Tag_names_match_in_any_case() =>
        await Assert.That(Trees.Dump("p <B>x</b>")).IsEqualTo("doc(p('p ',em*2('x')))");

    /// A code-like tag is a leaf: what Markdig parsed inside it is flattened to its text.
    [Test]
    [Arguments("code")]
    [Arguments("kbd")]
    [Arguments("tt")]
    [Arguments("samp")]
    public async Task A_code_like_pair_becomes_inline_code_holding_plain_text(string tag) =>
        await Assert.That(Trees.Dump($"p <{tag}>a *b*\nc</{tag}>")).IsEqualTo("doc(p('p ',code('a b c')))");

    [Test]
    public async Task An_anchor_becomes_a_link_and_one_without_a_target_unwraps() {
        await Assert.That(Trees.Dump("p <a href=\"https://e.example/x\">go</a>")).IsEqualTo("doc(p('p ',link[https://e.example/x]('go')))");
        await Assert.That(Trees.Dump("p <a name=\"top\">go</a>")).IsEqualTo("doc(p('p ','go'))");
    }

    /// The pass maps the target as written; refusing it is the link policy's job at render time.
    [Test]
    public async Task A_script_target_is_mapped_for_the_policy_to_refuse() =>
        await Assert.That(Trees.Dump("p <a href=\"javascript:alert(1)\">x</a>")).IsEqualTo("doc(p('p ',link[javascript:alert(1)]('x')))");

    [Test]
    public async Task Void_tags_convert_on_their_own_with_or_without_a_slash() {
        await Assert.That(Trees.Dump("a<br>b<br/>c")).IsEqualTo("doc(p('a',br,'b',br,'c'))");
        await Assert.That(Trees.Dump("p <img src=\"https://h/y.png\" alt=\"shot\">")).IsEqualTo("doc(p('p ',img[https://h/y.png]('shot')))");
        await Assert.That(Trees.Dump("p <img src=\"https://h/y.png\" />")).IsEqualTo("doc(p('p ',img[https://h/y.png]('y.png')))");
    }

    [Test]
    public async Task A_close_tag_for_a_void_tag_stays_source() {
        await Assert.That(Trees.Dump("a</br>b")).IsEqualTo("doc(p('a',tag('</br>'),'b'))");
        await Assert.That(Trees.Dump("a</img>b")).IsEqualTo("doc(p('a',tag('</img>'),'b'))");
    }

    [Test]
    public async Task A_trailing_slash_on_a_paired_tag_is_ignored() {
        await Assert.That(Trees.Dump("p <b/>x</b>")).IsEqualTo("doc(p('p ',em*2('x')))");
        await Assert.That(Trees.Dump("p <b/>x")).IsEqualTo("doc(p('p ',tag('<b/>'),'x'))");
    }

    [Test]
    public async Task Pairs_nest() =>
        await Assert.That(Trees.Dump("p <b><i>x</i></b>")).IsEqualTo("doc(p('p ',em*2(em*1('x'))))");

    /// Pins the crossing rule: `</b>` matches past the open `<i>`, which is then spent — it stays
    /// source inside the bold and can never pair with the `</i>` that follows.
    [Test]
    public async Task Crossing_tags_never_produce_crossing_nodes() {
        await Assert.That(Trees.Dump("p <b><i>x</b>y</i>")).IsEqualTo("doc(p('p ',em*2(tag('<i>'),'x'),'y',tag('</i>')))");
        await Assert.That(Trees.Dump("p <i><b><i>x</b>y</i>")).IsEqualTo("doc(p('p ',em*1(em*2(tag('<i>'),'x'),'y')))");
    }

    [Test]
    public async Task A_pair_split_across_two_containers_stays_source() =>
        await Assert.That(Trees.Dump("**<b>x**</b>")).IsEqualTo("doc(p(em*2(tag('<b>'),'x'),tag('</b>')))");

    [Test]
    public async Task Unmatched_and_unknown_tags_stay_source() {
        await Assert.That(Trees.Dump("a <b>x")).IsEqualTo("doc(p('a ',tag('<b>'),'x'))");
        await Assert.That(Trees.Dump("a x</b>")).IsEqualTo("doc(p('a x',tag('</b>')))");
        await Assert.That(Trees.Dump("a <span>x</span>")).IsEqualTo("doc(p('a ',tag('<span>'),'x',tag('</span>')))");
    }

    /// Block-only tags that Markdig parsed mid-paragraph have no block to become.
    [Test]
    public async Task Block_only_tags_in_a_paragraph_stay_source() {
        await Assert.That(Trees.Dump("prefix <pre>x</pre> suffix")).IsEqualTo("doc(p('prefix ',tag('<pre>'),'x',tag('</pre>'),' suffix'))");
        await Assert.That(Trees.Dump("prefix <details><summary>S</summary>x</details> suffix"))
            .IsEqualTo("doc(p('prefix ',tag('<details>'),tag('<summary>'),'S',tag('</summary>'),'x',tag('</details>'),' suffix'))");
        await Assert.That(Trees.Dump("<b><pre>x</pre></b>")).IsEqualTo("doc(p(em*2(tag('<pre>'),'x',tag('</pre>'))))");
    }

    [Test]
    public async Task An_inline_comment_is_removed() =>
        await Assert.That(Trees.Dump("a <!-- c --> b")).IsEqualTo("doc(p('a ',' b'))");

    /// Pins the two fallbacks of the depth budget in a paragraph: pairs convert from the inside
    /// out until the next one would pass the limit, and the rest stay source tags. The root inline
    /// container is at depth 2, so a wrap may reach 98 levels: 97 emphasis nodes over a literal.
    [Test]
    public async Task Nested_pairs_convert_up_to_the_depth_limit() {
        var markdown = "p " + string.Concat(Enumerable.Repeat("<b>", 200)) + "x" + string.Concat(Enumerable.Repeat("</b>", 200));
        var document = Trees.Parse(markdown);
        var dump = TreeDump.Of(document);
        await Assert.That(Trees.MaxDepth(document)).IsEqualTo(GitHubHtmlPass.MaxDepth);
        await Assert.That(Count(dump, "em*2(")).IsEqualTo(97);
        await Assert.That(Count(dump, "tag('<b>')")).IsEqualTo(103);
        await Assert.That(Count(dump, "tag('</b>')")).IsEqualTo(103);
    }

    /// The centre is normalised while the wrappers are still sibling tags, so the budget sees a
    /// link holding a literal and converts one pair fewer than around plain text.
    [Test]
    [Arguments("<https://example.com>", "link[https://example.com]('https://example.com')")]
    [Arguments("![](https://h/name.png)", "img[https://h/name.png]('name.png')")]
    [Arguments("<img src=\"https://h/name.png\">", "img[https://h/name.png]('name.png')")]
    public async Task The_budget_counts_what_normalisation_added(string centre, string expected) {
        var markdown = "p " + string.Concat(Enumerable.Repeat("<b>", 200)) + centre + string.Concat(Enumerable.Repeat("</b>", 200));
        var document = Trees.Parse(markdown);
        var dump = TreeDump.Of(document);
        await Assert.That(dump).Contains(expected);
        await Assert.That(Trees.MaxDepth(document)).IsEqualTo(GitHubHtmlPass.MaxDepth);
        await Assert.That(Count(dump, "em*2(")).IsEqualTo(96);
    }

    /// An `img` tag is refused like a wrap: under enough block quotes its label would pass the
    /// limit, and the tag stays source. One quote fewer and it converts.
    [Test]
    public async Task An_img_tag_is_refused_where_its_label_would_pass_the_depth_limit() {
        await Assert.That(TreeDump.Of(Trees.Parse(NormalisationTests.Quoted(97, "p <img src=\"https://h/name.png\">")))).Contains("tag('<img src=\"https://h/name.png\">')");
        await Assert.That(TreeDump.Of(Trees.Parse(NormalisationTests.Quoted(96, "p <img src=\"https://h/name.png\">")))).Contains("img[https://h/name.png]('name.png')");
    }

    /// Pins linear work: every close tag here looks for an open tag of a name that is not on the
    /// stack, past a hundred thousand that are. A search of the stack would be ten billion steps.
    [Test]
    [Timeout(30_000)]
    public async Task Many_unmatched_tags_stay_linear(CancellationToken _) {
        var markdown = "p " + string.Concat(Enumerable.Repeat("<i>", 100_000)) + string.Concat(Enumerable.Repeat("</b>", 100_000));
        var document = Trees.Parse(markdown);
        await Assert.That(Trees.MaxDepth(document)).IsEqualTo(3);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/InlinePairingTests/*"`
Expected: FAIL — the dumps still show `tag('<b>')` where the tests expect `em*2(`.

- [ ] **Step 3: Replace `InlinePairing.cs`**

```csharp
using Markdig.Syntax.Inlines;

namespace Capacitor.App.GitHubHtml;

/// The inline rule. Reach counts the levels a node occupies, its own included, so a wrapper
/// placed one level below its container fits when `containerDepth + reach` stays within the limit.
static class InlinePairing {
    sealed class OpenTag(string name, HtmlInline node, HtmlToken token, int index) {
        public string Name => name;
        public HtmlInline Node => node;
        public HtmlToken Token => token;
        public int Index => index;
        /// The greatest reach seen since this tag opened.
        public int Reach;
        /// False once a close tag matched past it: a spent open tag never pairs again.
        public bool Live = true;
    }

    /// Returns the reach of the container's children.
    public static int Process(ContainerInline container, int containerDepth) {
        var open = new List<OpenTag>();
        var byName = new Dictionary<string, Stack<OpenTag>>();
        var reach = 0;

        for (var child = container.FirstChild; child is not null;) {
            var next = child.NextSibling;
            if (child is HtmlInline html) Tag(html);
            else Note(child is ContainerInline nested ? 1 + Process(nested, containerDepth + 1) : 1);
            child = next;
        }
        foreach (var leftover in open) reach = Math.Max(reach, leftover.Reach);
        return reach;

        void Note(int levels) {
            if (open.Count > 0) open[^1].Reach = Math.Max(open[^1].Reach, levels);
            else reach = Math.Max(reach, levels);
        }

        void Tag(HtmlInline html) {
            var result = HtmlTokenizer.Tokenize(html.Tag);
            if (result.Malformed || result.Tokens.Count != 1) { Note(1); return; }
            var token = result.Tokens[0];
            if (token.Kind == HtmlTokenKind.Comment) { html.Remove(); return; }
            if (token.Kind == HtmlTokenKind.Text) { Note(1); return; }

            switch (HtmlTags.Classify(token.Name)) {
                case HtmlTagClass.Void when token.Kind == HtmlTokenKind.OpenTag:
                    Void(html, token);
                    break;
                case HtmlTagClass.Paired when HtmlTags.IsInline(token.Name) && token.Kind == HtmlTokenKind.OpenTag:
                    // Counted as source until a close tag proves otherwise.
                    Note(1);
                    var entry = new OpenTag(token.Name, html, token, open.Count);
                    open.Add(entry);
                    if (!byName.TryGetValue(token.Name, out var named)) byName[token.Name] = named = new();
                    named.Push(entry);
                    break;
                case HtmlTagClass.Paired when HtmlTags.IsInline(token.Name):
                    Close(html, token.Name);
                    break;
                default:
                    Note(1);
                    break;
            }
        }

        void Void(HtmlInline html, HtmlToken token) {
            if (token.Name == "br") {
                html.ReplaceBy(new LineBreakInline { IsHard = true }, copyChildren: false);
                Note(1);
                return;
            }
            if (containerDepth + 2 > GitHubHtmlPass.MaxDepth) { Note(1); return; }
            var image = new LinkInline(token.Attribute("src")?.Trim() ?? "", "") { IsImage = true };
            image.AppendChild(new LiteralInline(ImageLabel.For(token.Attribute("alt"), image.Url)));
            html.ReplaceBy(image, copyChildren: false);
            Note(2);
        }

        OpenTag? Find(string name) {
            if (!byName.TryGetValue(name, out var named)) return null;
            while (named.Count > 0 && !named.Peek().Live) named.Pop();
            return named.Count > 0 ? named.Peek() : null;
        }

        void Close(HtmlInline close, string name) {
            if (Find(name) is not { } match) { Note(1); return; }

            var inner = match.Reach;
            for (var i = open.Count - 1; i > match.Index; i--) {
                inner = Math.Max(inner, open[i].Reach);
                open[i].Live = false;
            }
            match.Live = false;
            open.RemoveRange(match.Index, open.Count - match.Index);

            var first = match.Node.NextSibling!;
            var empty = ReferenceEquals(first, close);

            if (HtmlTags.IsCode(name)) {
                var text = empty ? "" : InlineText.Collapse(InlineText.Plain(first, close));
                for (var inline = first; !ReferenceEquals(inline, close);) {
                    var following = inline.NextSibling!;
                    inline.Remove();
                    inline = following;
                }
                close.Remove();
                if (text.Length == 0) { match.Node.Remove(); return; }
                match.Node.ReplaceBy(new CodeInline(text), copyChildren: false);
                Note(1);
                return;
            }

            var target = name == "a" ? match.Token.Attribute("href")?.Trim() : null;
            if (name == "a" && string.IsNullOrEmpty(target)) {
                match.Node.Remove();
                close.Remove();
                Note(inner);
                return;
            }

            var wrapperReach = empty ? 1 : 1 + inner;
            if (containerDepth + wrapperReach > GitHubHtmlPass.MaxDepth) { Note(Math.Max(1, inner)); return; }

            ContainerInline wrapper = name == "a" ? new LinkInline(target!, "") : Emphasis(name);
            for (var inline = first; !ReferenceEquals(inline, close);) {
                var following = inline.NextSibling!;
                inline.Remove();
                wrapper.AppendChild(inline);
                inline = following;
            }
            match.Node.ReplaceBy(wrapper, copyChildren: false);
            close.Remove();
            Note(wrapperReach);
        }
    }

    static EmphasisInline Emphasis(string name) {
        HtmlTags.TryEmphasis(name, out var delimiter, out var count);
        return new EmphasisInline { DelimiterChar = delimiter, DelimiterCount = count };
    }
}
```

Why the walk is safe to mutate under: `next` is read before a child is handled, and handling a close tag only ever removes that tag and siblings *before* it, so `next` stays attached. `Process` recurses by inline nesting, which the parser's limit and `MaxDepth` bound; nothing recurses per tag.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/InlinePairingTests/*"` and then `NormalisationTests` again.
Expected: PASS (32 and 6 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.App/GitHubHtml/InlinePairing.cs test/Capacitor.App.Tests.Unit/GitHubHtml/InlinePairingTests.cs
git commit -m "Convert paired inline HTML tags into markdown nodes (#982)" -m "A close tag that matches past an open tag spends it, so no synthesized node crosses another; every wrap is budgeted before it moves anything." -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 5: Link hoisting

MarkView dispatches a click only for a hyperlink that is a direct inline of its text block, so the pass lifts every link above its emphasis ancestors: `Emphasis[a, Link[b], c]` becomes `Emphasis[a]`, `Link[Emphasis[b]]`, `Emphasis[c]`. The rendering is identical.

**Files:**
- Create: `src/Capacitor.App/GitHubHtml/LinkHoister.cs`
- Modify: `src/Capacitor.App/GitHubHtml/InlinePass.cs`
- Test: `test/Capacitor.App.Tests.Unit/GitHubHtml/LinkHoistingTests.cs`

**Interfaces:**
- Consumes: `GitHubHtmlPass.MaxHoistedEmphasis`.
- Produces: `LinkHoister.Hoist(ContainerInline root)` — also called by `InlineBuilder` in Task 6.

- [ ] **Step 1: Write the failing tests**

`test/Capacitor.App.Tests.Unit/GitHubHtml/LinkHoistingTests.cs`:

```csharp
using Capacitor.App.GitHubHtml;

namespace Capacitor.App.Tests.Unit.GitHubHtml;

public class LinkHoistingTests {
    static string Wrapped(int levels, string centre) =>
        "p " + string.Concat(Enumerable.Repeat("<b>", levels)) + centre + string.Concat(Enumerable.Repeat("</b>", levels));

    [Test]
    public async Task A_link_rises_above_its_emphasis_and_the_emphasis_splits_around_it() =>
        await Assert.That(Trees.Dump("**a [b](https://u.example) c**"))
            .IsEqualTo("doc(p(em*2('a '),link[https://u.example](em*2('b')),em*2(' c')))");

    [Test]
    public async Task An_only_child_leaves_no_empty_copies() =>
        await Assert.That(Trees.Dump("**[b](https://u.example)**")).IsEqualTo("doc(p(link[https://u.example](em*2('b'))))");

    /// Pins the order of the copies: the outer emphasis stays outermost inside the link.
    [Test]
    public async Task Nested_formatting_keeps_its_order_and_delimiters_inside_the_link() {
        var document = Trees.Parse("p <b><i><a href=\"https://u.example\">x</a></i></b>");
        await Assert.That(TreeDump.Of(document)).IsEqualTo("doc(p('p ',link[https://u.example](em*2(em*1('x')))))");
        await Assert.That(Trees.MaxDepth(document)).IsEqualTo(6);
    }

    [Test]
    public async Task An_emphasised_image_keeps_its_emphasis_around_its_label() {
        await Assert.That(Trees.Dump("**![badge](https://h/b.png)**")).IsEqualTo("doc(p(img[https://h/b.png](em*2('badge'))))");
        await Assert.That(Trees.Dump("**![](https://h/name.png)**")).IsEqualTo("doc(p(img[https://h/name.png](em*2('name.png'))))");
    }

    [Test]
    public async Task An_emphasised_autolink_is_hoisted_like_any_link() {
        await Assert.That(Trees.Dump("**<https://example.com>**")).IsEqualTo("doc(p(link[https://example.com](em*2('https://example.com'))))");
        await Assert.That(Trees.Dump("p <b><https://example.com></b>")).IsEqualTo("doc(p('p ',link[https://example.com](em*2('https://example.com'))))");
    }

    /// A link inside a link rises to the outer link's children and no further: which of the two
    /// becomes the hyperlink is the renderer's decision.
    [Test]
    public async Task A_link_inside_a_link_stops_below_the_outer_link() =>
        await Assert.That(Trees.Dump("p <a href=\"https://outer.example\">a <b><a href=\"https://inner.example\">b</a></b> c</a>"))
            .IsEqualTo("doc(p('p ',link[https://outer.example]('a ',link[https://inner.example](em*2('b')),' c')))");

    [Test]
    public async Task A_link_under_more_than_eight_emphasis_ancestors_stays_put() {
        await Assert.That(Trees.Dump(Wrapped(8, "<a href=\"https://u.example\">x</a>"))).StartsWith("doc(p('p ',link[https://u.example](");
        await Assert.That(Trees.Dump(Wrapped(9, "<a href=\"https://u.example\">x</a>"))).Contains("em*2(link[https://u.example]('x'))");
    }

    /// A childless image is one whose label the depth budget refused; its renderer writes the
    /// label, which inherits the emphasis only while the image stays inside it.
    [Test]
    public async Task A_childless_image_is_never_hoisted() =>
        await Assert.That(TreeDump.Of(Trees.Parse(NormalisationTests.Quoted(96, "**![](https://h/name.png)**")))).Contains("em*2(img[https://h/name.png]())");

    /// Pins the bound on what one comment can make the pass create: a hoist copies its chain at
    /// most twice, so 2 000 links under 8 levels add no more than 16 emphasis nodes each.
    [Test]
    [Timeout(30_000)]
    public async Task Hoisting_many_sibling_links_stays_bounded(CancellationToken _) {
        var links = string.Concat(Enumerable.Range(0, 2_000).Select(n => $"<a href=\"https://u.example/{n}\">x</a> "));
        var dump = Trees.Dump(Wrapped(8, links));
        await Assert.That(dump.Split("link[").Length - 1).IsEqualTo(2_000);
        await Assert.That(dump.Split("em*2(").Length - 1).IsLessThanOrEqualTo(8 + 16 * 2_000);
        await Assert.That(dump).DoesNotContain("em*2(link[");
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/LinkHoistingTests/*"`
Expected: FAIL — the first test shows `doc(p(em*2('a ',link[https://u.example]('b'),' c')))`.

- [ ] **Step 3: Write `LinkHoister.cs`**

```csharp
using Markdig.Syntax.Inlines;

namespace Capacitor.App.GitHubHtml;

/// Lifts links above their emphasis ancestors. MarkView activates only a hyperlink that is a
/// direct inline of its text block, and the emphasis renders the same from inside the link.
static class LinkHoister {
    public static void Hoist(ContainerInline root) {
        var links = new List<LinkInline>();
        Collect(root, links);
        // Last link first: what follows a link inside its emphasis moves to a trailing copy, and
        // going backwards leaves each inline to be moved once rather than once per earlier link.
        for (var i = links.Count - 1; i >= 0; i--) HoistOne(links[i]);
    }

    static void Collect(ContainerInline container, List<LinkInline> links) {
        for (var child = container.FirstChild; child is not null; child = child.NextSibling) {
            if (child is LinkInline link) links.Add(link);
            if (child is ContainerInline nested) Collect(nested, links);
        }
    }

    static void HoistOne(LinkInline link) {
        // A childless image gets its label from the renderer, which inherits the emphasis around it.
        if (link.FirstChild is null) return;

        var levels = 0;
        var top = link.Parent;
        while (top is EmphasisInline) {
            levels++;
            top = top.Parent;
        }
        if (levels == 0 || levels > GitHubHtmlPass.MaxHoistedEmphasis || top is null) return;
        // Above the emphasis must be the block's root container or an enclosing link.
        if (top.Parent is not null && top is not LinkInline) return;

        while (link.Parent is EmphasisInline emphasis) LiftOver(link, emphasis);
    }

    static void LiftOver(LinkInline link, EmphasisInline emphasis) {
        link.EmbraceChildrenBy(Copy(emphasis));

        EmphasisInline? tail = null;
        for (var inline = link.NextSibling; inline is not null;) {
            var following = inline.NextSibling;
            inline.Remove();
            (tail ??= Copy(emphasis)).AppendChild(inline);
            inline = following;
        }

        link.Remove();
        emphasis.InsertAfter(link);
        if (tail is not null) link.InsertAfter(tail);
        if (emphasis.FirstChild is null) emphasis.Remove();
    }

    static EmphasisInline Copy(EmphasisInline emphasis) =>
        new() { DelimiterChar = emphasis.DelimiterChar, DelimiterCount = emphasis.DelimiterCount };
}
```

- [ ] **Step 4: Call it from `InlinePass.Process`**

In `src/Capacitor.App/GitHubHtml/InlinePass.cs`, replace the last line of `Process`:

```csharp
        Normalise(root, rootDepth);
        var reach = InlinePairing.Process(root, rootDepth);
        // Hoisting moves nodes sideways and never deepens the tree, so the reach still holds.
        LinkHoister.Hoist(root);
        return 1 + reach;
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/LinkHoistingTests/*"`, then `InlinePairingTests` and `NormalisationTests` again.
Expected: PASS (9, 32 and 6 tests).

- [ ] **Step 6: Commit**

```bash
git add src/Capacitor.App/GitHubHtml/LinkHoister.cs src/Capacitor.App/GitHubHtml/InlinePass.cs test/Capacitor.App.Tests.Unit/GitHubHtml/LinkHoistingTests.cs
git commit -m "Hoist links above their emphasis so MarkView can activate them (#982)" -m "MarkView dispatches a click only for a hyperlink that is a direct inline of its text block. Hoisting is capped at 8 levels: each hoist copies its ancestor chain twice." -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 6: The block rule — paragraphs, `<pre>` and comments

An `HtmlBlock` carries raw lines only. It is tokenized once and either converts as a whole or is left untouched. This task converts blocks without `details` tags; a block that holds one stays source until Task 7.

**Files:**
- Create: `src/Capacitor.App/GitHubHtml/HtmlBlockPartKind.cs`, `HtmlBlockPart.cs`, `HtmlBlockPlan.cs`, `HtmlBlockReader.cs`, `InlineBuildMode.cs`, `InlineBuilder.cs`, `HtmlPreBlock.cs`, `DetailsFolder.cs`
- Modify: `src/Capacitor.App/GitHubHtml/GitHubHtmlPass.cs`
- Modify: `test/Capacitor.App.Tests.Unit/GitHubHtml/TreeDump.cs` (add the `HtmlPreBlock` case)
- Test: `test/Capacitor.App.Tests.Unit/GitHubHtml/HtmlBlockRuleTests.cs`

**Interfaces:**
- Consumes: `HtmlTokenizer`, `HtmlTags`, `InlineText.Collapse`, `ImageLabel.For`, `LinkHoister.Hoist`, `InlinePass.Process`.
- Produces:
  - `HtmlBlockPartKind { DetailsOpen, DetailsClose, Summary, Paragraph, Pre }`
  - `HtmlBlockPart(HtmlBlockPartKind Kind, IReadOnlyList<HtmlToken> Tokens, bool IsOpen, int Reach)` — `Reach` is the levels a `Paragraph` or `Pre` occupies counting its block node, and for a `Summary` the levels below its details node.
  - `HtmlBlockPlan { bool Rejected; List<HtmlBlockPart> Parts; List<bool> DetailsTags }` — `DetailsTags` lists every `details` tag of the block in order (true = open tag) and is complete even when the block is rejected.
  - `HtmlBlockReader.Read(HtmlBlock) : HtmlBlockPlan`
  - `InlineBuildMode { Paragraph, Summary, Pre }`; `InlineBuilder.Build(IReadOnlyList<HtmlToken>, InlineBuildMode) : ContainerInline`
  - `HtmlPreBlock(ContainerInline inlines) : LeafBlock`
  - `DetailsFolder.Item(Block block, HtmlBlockPlan? plan, int reach)`; `DetailsFolder.Fold(ContainerBlock container, int depth, List<DetailsFolder.Item> items) : int` (returns the reach of the container's children)

- [ ] **Step 1: Write the failing tests**

Every block fixture asserts `Trees.Shape` first: whether Markdig makes an `HtmlBlock` or a paragraph of a line decides which rule the fixture exercises. A tag alone on the first line opens a block that runs to the next blank line; `<pre` opens one that runs to the line holding `</pre>`.

`test/Capacitor.App.Tests.Unit/GitHubHtml/HtmlBlockRuleTests.cs`:

```csharp
using Capacitor.App.GitHubHtml;
using Markdig.Syntax;

namespace Capacitor.App.Tests.Unit.GitHubHtml;

public class HtmlBlockRuleTests {
    static async Task Untouched(string markdown) {
        await Assert.That(Trees.Shape(markdown)).StartsWith("doc(html(");
        await Assert.That(Trees.Dump(markdown)).IsEqualTo(Trees.Shape(markdown));
    }

    [Test]
    public async Task An_image_alone_on_a_line_is_a_block_and_converts() {
        const string markdown = "<img src=\"https://img.shields.io/badge/Medium-634FD1\" height=\"20px\" alt=\"Remediation recommended\">";
        await Assert.That(Trees.Shape(markdown)).StartsWith("doc(html(");
        await Assert.That(Trees.Dump(markdown)).IsEqualTo("doc(p(img[https://img.shields.io/badge/Medium-634FD1]('Remediation recommended')))");
    }

    /// Whitespace collapses as HTML does and is trimmed at the paragraph's edges, inside the
    /// formatting that opens and closes it too.
    [Test]
    public async Task Inline_content_becomes_a_paragraph_with_collapsed_whitespace() {
        const string markdown = "<b>\nbold   text\n</b>";
        await Assert.That(Trees.Shape(markdown)).IsEqualTo("doc(html('<b>\\nbold   text\\n</b>'))");
        await Assert.That(Trees.Dump(markdown)).IsEqualTo("doc(p(em*2('bold text')))");
    }

    [Test]
    public async Task One_unknown_tag_rejects_the_whole_block() {
        await Untouched("<b>\nbold <span>x</span>\n</b>");
        await Assert.That(Trees.Dump("<b>\nbold x\n</b>")).IsEqualTo("doc(p(em*2('bold x')))");
    }

    [Test]
    [Arguments("<b>\n<i>x</b></i>")]
    [Arguments("<b>\nx")]
    [Arguments("</b>\nx")]
    [Arguments("<summary>S</summary>")]
    [Arguments("<b>\n<pre>x</pre>\n</b>")]
    [Arguments("<b>\nx</br>\n</b>")]
    [Arguments("<b>\nx</img>\n</b>")]
    [Arguments("<b>\nx <a href=\"\n</b>")]
    public async Task A_block_that_does_not_nest_properly_stays_source(string markdown) => await Untouched(markdown);

    [Test]
    public async Task A_trailing_slash_on_a_paired_tag_is_ignored() {
        await Assert.That(Trees.Dump("<b/>\nx</b>")).IsEqualTo("doc(p(em*2('x')))");
        await Untouched("<b/>\nx");
    }

    [Test]
    public async Task Entities_decode_in_text_and_links_are_mapped_and_hoisted() {
        await Assert.That(Trees.Dump("<b>\nA &amp; B\n</b>")).IsEqualTo("doc(p(em*2('A & B')))");
        await Assert.That(Trees.Dump("<b>\n<a href=\"javascript:alert(1)\">x</a>\n</b>")).IsEqualTo("doc(p(link[javascript:alert(1)](em*2('x'))))");
    }

    [Test]
    public async Task Void_tags_convert_inside_a_block() =>
        await Assert.That(Trees.Dump("<b>\na<br>b<br/>c\n</b>")).IsEqualTo("doc(p(em*2('a',br,'b',br,'c')))");

    /// Pins `pre`: whitespace and inner formatting kept, entities decoded, the line end after the
    /// open tag and the one before the close tag dropped, every other line end a hard break.
    [Test]
    public async Task Pre_keeps_whitespace_formatting_and_entities() {
        const string markdown = "<pre>\nThe <b><i>repo&#x27;s</i></b> begins\n  indented\n</pre>";
        await Assert.That(Trees.Shape(markdown)).StartsWith("doc(html(");
        await Assert.That(Trees.Dump(markdown)).IsEqualTo("doc(pre('The ',em*2(em*1('repo's')),' begins',br,'  indented'))");
    }

    [Test]
    public async Task Code_inside_pre_splits_at_line_ends() {
        await Assert.That(Trees.Shape("<pre><code>a\n\nb</code></pre>")).StartsWith("doc(html(");
        await Assert.That(Trees.Dump("<pre><code>a\n\nb</code></pre>")).IsEqualTo("doc(pre(code('a'),br,br,code('b')))");
    }

    [Test]
    public async Task Decoded_line_ends_split_too() {
        await Assert.That(Trees.Dump("<pre>a&#10;b</pre>")).IsEqualTo("doc(pre('a',br,'b'))");
        await Assert.That(Trees.Dump("<pre>a&#13;&#10;b</pre>")).IsEqualTo("doc(pre('a',br,'b'))");
        await Assert.That(Trees.Dump("<b>\na&#10;b\n</b>")).IsEqualTo("doc(p(em*2('a b')))");
    }

    [Test]
    public async Task Void_tags_are_accepted_inside_pre() =>
        await Assert.That(Trees.Dump("<pre>a<br>b <img src=\"https://h/i.png\"></pre>")).IsEqualTo("doc(pre('a',br,'b ',img[https://h/i.png]('i.png')))");

    [Test]
    public async Task A_link_inside_formatting_inside_pre_is_hoisted() =>
        await Assert.That(Trees.Dump("<pre><b><a href=\"https://u.example\">x</a></b></pre>")).IsEqualTo("doc(pre(link[https://u.example](em*2('x'))))");

    /// Comments go token by token: a comment-type block keeps the whole of its closing line, so
    /// dropping the block would drop the visible text after the comment.
    [Test]
    public async Task Comments_are_removed_where_the_block_converts() {
        await Assert.That(Trees.Shape("<!-- c -->")).StartsWith("doc(html(");
        await Assert.That(Trees.Dump("<!-- c -->")).IsEqualTo("doc()");
        await Assert.That(Trees.Shape("<!-- m -->Visible text")).StartsWith("doc(html(");
        await Assert.That(Trees.Dump("<!-- m -->Visible text")).IsEqualTo("doc(p('Visible text'))");
        await Assert.That(Trees.Dump("before\n\n<!-- c -->\n\nafter")).IsEqualTo("doc(p('before'),p('after'))");
    }

    [Test]
    public async Task Comments_stay_where_the_block_is_rejected() {
        await Untouched("<div><!-- m --></div>");
        await Untouched("<!-- never closed\ntext");
    }

    /// Pins the block's own fallback at the depth limit: the whole block stays source, where a
    /// paragraph converts pair by pair.
    [Test]
    public async Task A_block_nested_past_the_depth_limit_is_rejected_whole() =>
        await Untouched("<b>\n" + string.Concat(Enumerable.Repeat("<b>", 199)) + "x" + string.Concat(Enumerable.Repeat("</b>", 200)));

    [Test]
    public async Task No_synthesized_text_holds_a_line_end() {
        string[] fixtures = [
            "<b>\nbold\ntext\n</b>", "<pre>\na\r\nb\n</pre>", "<pre><code>a\n\nb</code></pre>", "<pre>a&#13;&#10;b</pre>",
            "<img src=\"https://h/a%0Ab.png\">", "<b>\n<code>a\nb</code>\n</b>", "<img alt=\"two\nlines\" src=\"https://h/x.png\">",
        ];
        foreach (var fixture in fixtures) {
            var document = Trees.Parse(fixture);
            foreach (var literal in document.Descendants<Markdig.Syntax.Inlines.LiteralInline>())
                await Assert.That(literal.Content.ToString().AsSpan().IndexOfAny('\r', '\n')).IsEqualTo(-1);
            foreach (var code in document.Descendants<Markdig.Syntax.Inlines.CodeInline>())
                await Assert.That(code.Content.AsSpan().IndexOfAny('\r', '\n')).IsEqualTo(-1);
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/HtmlBlockRuleTests/*"`
Expected: FAIL — every converting fixture still dumps as `doc(html(…))`.

- [ ] **Step 3: Write the plan types**

`src/Capacitor.App/GitHubHtml/HtmlBlockPartKind.cs`:

```csharp
namespace Capacitor.App.GitHubHtml;

enum HtmlBlockPartKind { DetailsOpen, DetailsClose, Summary, Paragraph, Pre }
```

`src/Capacitor.App/GitHubHtml/HtmlBlockPart.cs`:

```csharp
namespace Capacitor.App.GitHubHtml;

/// One thing an HTML block would become. `Reach` counts levels: for a paragraph or a `pre`, its
/// block node included; for a summary, the levels below its details node.
sealed record HtmlBlockPart(HtmlBlockPartKind Kind, IReadOnlyList<HtmlToken> Tokens, bool IsOpen, int Reach);
```

`src/Capacitor.App/GitHubHtml/HtmlBlockPlan.cs`:

```csharp
namespace Capacitor.App.GitHubHtml;

sealed class HtmlBlockPlan {
    public bool Rejected { get; set; }

    public List<HtmlBlockPart> Parts { get; } = [];

    /// Every `details` tag of the block in order, true for an open tag. Complete even when the
    /// block is rejected: matching reads every block, so a rejected one cannot change who pairs.
    public List<bool> DetailsTags { get; } = [];
}
```

`src/Capacitor.App/GitHubHtml/InlineBuildMode.cs`:

```csharp
namespace Capacitor.App.GitHubHtml;

enum InlineBuildMode { Paragraph, Summary, Pre }
```

`src/Capacitor.App/GitHubHtml/HtmlPreBlock.cs`:

```csharp
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Capacitor.App.GitHubHtml;

/// A `pre` element: inline formatting kept, line structure as hard breaks.
public sealed class HtmlPreBlock : LeafBlock {
    public HtmlPreBlock(ContainerInline inlines) : base(null) => Inline = inlines;
}
```

- [ ] **Step 4: Write `HtmlBlockReader.cs`**

```csharp
using Markdig.Syntax;

namespace Capacitor.App.GitHubHtml;

/// The block rule: one pass over a block's tokens that either describes everything the block
/// would become or rejects it whole. Nothing is built here, so a rejected block costs a scan.
static class HtmlBlockReader {
    public static HtmlBlockPlan Read(HtmlBlock block) {
        var plan = new HtmlBlockPlan();
        var result = HtmlTokenizer.Tokenize(Source(block));
        if (result.Malformed) plan.Rejected = true;
        new Reader(plan).Run(result.Tokens);
        return plan;
    }

    static string Source(HtmlBlock block) {
        var lines = new string[block.Lines.Count];
        for (var i = 0; i < lines.Length; i++) lines[i] = block.Lines.Lines[i].Slice.ToString();
        return string.Join('\n', lines);
    }

    sealed class Reader(HtmlBlockPlan plan) {
        readonly Stack<string> _open = new();
        List<HtmlToken> _content = [];
        int _inlineLevels;
        int _deepest;
        bool _hasContent;
        bool _afterDetailsOpen;

        public void Run(IReadOnlyList<HtmlToken> tokens) {
            foreach (var token in tokens) {
                var isTag = token.Kind is HtmlTokenKind.OpenTag or HtmlTokenKind.CloseTag;
                if (isTag && HtmlTags.Classify(token.Name) == HtmlTagClass.Structural) Details(token);
                else if (!plan.Rejected) Step(token);
            }
            if (plan.Rejected) return;
            if (_open.Count > 0) { plan.Rejected = true; return; }
            FlushParagraph();
        }

        /// A `details` tag is never pushed or popped: it needs nothing open around it and is
        /// matched across blocks later.
        void Details(HtmlToken token) {
            var opens = token.Kind == HtmlTokenKind.OpenTag;
            plan.DetailsTags.Add(opens);
            if (plan.Rejected) return;
            if (_open.Count > 0) { plan.Rejected = true; return; }
            FlushParagraph();
            plan.Parts.Add(new(opens ? HtmlBlockPartKind.DetailsOpen : HtmlBlockPartKind.DetailsClose, [], opens && token.Attributes.ContainsKey("open"), 0));
            _afterDetailsOpen = opens;
        }

        void Step(HtmlToken token) {
            switch (token.Kind) {
                case HtmlTokenKind.Comment:
                    return;
                case HtmlTokenKind.Text:
                    if (!token.IsWhitespace) {
                        _afterDetailsOpen = false;
                        _hasContent = true;
                        Leaf(1);
                    }
                    _content.Add(token);
                    return;
            }

            var opens = token.Kind == HtmlTokenKind.OpenTag;
            switch (HtmlTags.Classify(token.Name)) {
                case HtmlTagClass.Void when opens:
                    _afterDetailsOpen = false;
                    _hasContent = true;
                    Leaf(token.Name == "img" ? 2 : 1);
                    _content.Add(token);
                    return;
                case HtmlTagClass.Paired when token.Name == "summary":
                    Summary(opens);
                    return;
                case HtmlTagClass.Paired when token.Name == "pre":
                    Pre(opens);
                    return;
                case HtmlTagClass.Paired:
                    Inline(token, opens);
                    return;
                default:
                    plan.Rejected = true;
                    return;
            }
        }

        /// Accepted only as the first thing after its `details` open tag, in the same block, once.
        void Summary(bool opens) {
            if (opens) {
                if (!_afterDetailsOpen || _open.Count > 0) { plan.Rejected = true; return; }
                _afterDetailsOpen = false;
                _open.Push("summary");
                Begin();
                return;
            }
            if (_open.Count == 0 || _open.Peek() != "summary") { plan.Rejected = true; return; }
            _open.Pop();
            Emit(HtmlBlockPartKind.Summary, 1 + _deepest);
        }

        void Pre(bool opens) {
            if (opens) {
                if (_open.Count > 0) { plan.Rejected = true; return; }
                _afterDetailsOpen = false;
                FlushParagraph();
                _open.Push("pre");
                return;
            }
            if (_open.Count == 0 || _open.Peek() != "pre") { plan.Rejected = true; return; }
            _open.Pop();
            Emit(HtmlBlockPartKind.Pre, 2 + _deepest);
        }

        void Inline(HtmlToken token, bool opens) {
            if (opens) {
                _afterDetailsOpen = false;
                _open.Push(token.Name);
                // A block nested this far can never fit, and rejecting here keeps the builder shallow.
                if (++_inlineLevels > GitHubHtmlPass.MaxDepth) plan.Rejected = true;
            } else {
                if (_open.Count == 0 || _open.Peek() != token.Name) { plan.Rejected = true; return; }
                _open.Pop();
                _inlineLevels--;
            }
            _content.Add(token);
        }

        void Leaf(int levels) => _deepest = Math.Max(_deepest, _inlineLevels + levels);

        void FlushParagraph() {
            if (_hasContent) Emit(HtmlBlockPartKind.Paragraph, 2 + _deepest);
            else Begin();
        }

        void Emit(HtmlBlockPartKind kind, int reach) {
            plan.Parts.Add(new(kind, _content, false, reach));
            Begin();
        }

        void Begin() {
            _content = [];
            _deepest = 0;
            _hasContent = false;
        }
    }
}
```

- [ ] **Step 5: Write `InlineBuilder.cs`**

```csharp
using System.Net;
using System.Text;
using Markdig.Helpers;
using Markdig.Syntax.Inlines;

namespace Capacitor.App.GitHubHtml;

/// Turns the tokens of one accepted part into inlines. The reader has already checked that the
/// tags nest, so a close tag always has its open tag on the stack.
static class InlineBuilder {
    public static ContainerInline Build(IReadOnlyList<HtmlToken> tokens, InlineBuildMode mode) {
        var root = new ContainerInline();
        new Builder(root, mode).Run(tokens);
        if (mode != InlineBuildMode.Pre) {
            TrimStart(root);
            TrimEnd(root);
        }
        // A summary is a button's label: it has no links to hoist.
        if (mode != InlineBuildMode.Summary) LinkHoister.Hoist(root);
        return root;
    }

    sealed class Builder(ContainerInline root, InlineBuildMode mode) {
        /// Null for a tag that adds no node: an anchor without a target, any anchor in a summary.
        readonly Stack<ContainerInline?> _frames = new();
        ContainerInline _target = root;
        StringBuilder? _code;
        int _codeDepth;

        public void Run(IReadOnlyList<HtmlToken> tokens) {
            for (var i = 0; i < tokens.Count; i++) {
                var token = tokens[i];
                switch (token.Kind) {
                    case HtmlTokenKind.Text:
                        Text(token.Text, first: i == 0, last: i == tokens.Count - 1);
                        break;
                    case HtmlTokenKind.OpenTag when HtmlTags.Classify(token.Name) == HtmlTagClass.Void:
                        Void(token);
                        break;
                    case HtmlTokenKind.OpenTag:
                        Open(token);
                        break;
                    case HtmlTokenKind.CloseTag:
                        Close();
                        break;
                }
            }
        }

        void Text(string raw, bool first, bool last) {
            var text = WebUtility.HtmlDecode(raw);
            if (mode != InlineBuildMode.Pre) { Append(InlineText.Collapse(text)); return; }

            text = text.ReplaceLineEndings("\n");
            // Browsers show neither the line end after `<pre>` nor the one before `</pre>`.
            if (first && text.StartsWith('\n')) text = text[1..];
            if (last && text.EndsWith('\n')) text = text[..^1];
            var lines = text.Split('\n');
            for (var i = 0; i < lines.Length; i++) {
                if (i > 0) Break();
                Append(lines[i]);
            }
        }

        void Append(string text) {
            if (_code is not null) _code.Append(text);
            else if (text.Length > 0) _target.AppendChild(new LiteralInline(text));
        }

        void Break() {
            FlushCode();
            _target.AppendChild(new LineBreakInline { IsHard = true });
        }

        void Void(HtmlToken token) {
            if (token.Name == "br") {
                if (_code is not null && mode != InlineBuildMode.Pre) _code.Append(' ');
                else Break();
                return;
            }
            var source = token.Attribute("src")?.Trim() ?? "";
            var label = ImageLabel.For(token.Attribute("alt"), source);
            if (_code is not null || mode == InlineBuildMode.Summary) { Append(label); return; }
            var image = new LinkInline(source, "") { IsImage = true };
            image.AppendChild(new LiteralInline(label));
            _target.AppendChild(image);
        }

        void Open(HtmlToken token) {
            // Inside a code-like tag everything flattens to text; nested tags only need counting.
            if (_code is not null) { _codeDepth++; return; }
            if (HtmlTags.IsCode(token.Name)) { _code = new(); _codeDepth = 1; return; }

            if (token.Name == "a") {
                var target = token.Attribute("href")?.Trim();
                Push(mode == InlineBuildMode.Summary || string.IsNullOrEmpty(target) ? null : new LinkInline(target, ""));
                return;
            }
            HtmlTags.TryEmphasis(token.Name, out var delimiter, out var count);
            Push(new EmphasisInline { DelimiterChar = delimiter, DelimiterCount = count });
        }

        void Push(ContainerInline? node) {
            _frames.Push(node);
            if (node is null) return;
            _target.AppendChild(node);
            _target = node;
        }

        void Close() {
            if (_code is not null) {
                if (--_codeDepth > 0) return;
                FlushCode();
                _code = null;
                return;
            }
            if (_frames.Pop() is { } node) _target = node.Parent!;
        }

        void FlushCode() {
            if (_code is null || _code.Length == 0) return;
            var text = mode == InlineBuildMode.Pre ? _code.ToString() : InlineText.Collapse(_code.ToString());
            _code.Clear();
            if (text.Length > 0) _target.AppendChild(new CodeInline(text));
        }
    }

    static void TrimStart(ContainerInline container) {
        while (container.FirstChild is { } first) {
            if (first is LiteralInline literal) {
                var trimmed = literal.Content.ToString().TrimStart();
                if (trimmed.Length == 0) { literal.Remove(); continue; }
                literal.Content = new StringSlice(trimmed);
                return;
            }
            if (first is ContainerInline nested and not LinkInline { IsImage: true }) TrimStart(nested);
            return;
        }
    }

    static void TrimEnd(ContainerInline container) {
        while (container.LastChild is { } last) {
            if (last is LiteralInline literal) {
                var trimmed = literal.Content.ToString().TrimEnd();
                if (trimmed.Length == 0) { literal.Remove(); continue; }
                literal.Content = new StringSlice(trimmed);
                return;
            }
            if (last is ContainerInline nested and not LinkInline { IsImage: true }) TrimEnd(nested);
            return;
        }
    }
}
```

`Break()` inside a code-like tag in `pre` flushes the collected segment first, which is how `<pre><code>a\n\nb</code></pre>` becomes code, two breaks, code. `TrimStart` and `TrimEnd` recurse by inline nesting, which the reader capped at `MaxDepth`.

- [ ] **Step 6: Write `DetailsFolder.cs` (this task's version) and rewire the block walk**

`src/Capacitor.App/GitHubHtml/DetailsFolder.cs` — Task 7 replaces this file with the version that matches and folds `details`; here a block holding one is simply rejected:

```csharp
using Markdig.Syntax;

namespace Capacitor.App.GitHubHtml;

/// Rebuilds one block container from its children and the plans of its HTML blocks. Both walks
/// share their arithmetic: the first decides what fits, the second builds it.
static class DetailsFolder {
    public sealed class Item(Block block, HtmlBlockPlan? plan, int reach) {
        public Block Block => block;
        public HtmlBlockPlan? Plan => plan;
        /// The levels the block occupies if it stays as it is.
        public int Reach => reach;
        public bool Converts => plan is { Rejected: false };
    }

    /// Returns the reach of the container's children.
    public static int Fold(ContainerBlock container, int depth, List<Item> items) {
        foreach (var item in items)
            if (item.Plan is { DetailsTags.Count: > 0 } plan) plan.Rejected = true;

        if (!items.Exists(item => item.Converts)) return Walk(container, depth, items, apply: false);
        Walk(container, depth, items, apply: false);
        return Walk(container, depth, items, apply: true);
    }

    static int Walk(ContainerBlock container, int depth, List<Item> items, bool apply) {
        var reach = 0;
        if (apply) container.Clear();

        foreach (var item in items) {
            if (!item.Converts) {
                if (apply) container.Add(item.Block);
                reach = Math.Max(reach, item.Reach);
                continue;
            }
            foreach (var part in item.Plan!.Parts) {
                // A block placed in the container sits at depth + 1, so its deepest node is at depth + reach.
                if (!apply && depth + part.Reach > GitHubHtmlPass.MaxDepth) { item.Plan.Rejected = true; break; }
                if (apply) container.Add(Build(part));
                reach = Math.Max(reach, part.Reach);
            }
        }
        return reach;
    }

    static Block Build(HtmlBlockPart part) => part.Kind == HtmlBlockPartKind.Pre
        ? new HtmlPreBlock(InlineBuilder.Build(part.Tokens, InlineBuildMode.Pre))
        : new ParagraphBlock { Inline = InlineBuilder.Build(part.Tokens, InlineBuildMode.Paragraph) };
}
```

In `src/Capacitor.App/GitHubHtml/GitHubHtmlPass.cs`, replace `ProcessContainer`:

```csharp
    /// Returns the reach of the container's children.
    static int ProcessContainer(ContainerBlock container, int depth) {
        var items = new List<DetailsFolder.Item>(container.Count);
        foreach (var child in container) {
            items.Add(child switch {
                HtmlBlock html        => new DetailsFolder.Item(child, HtmlBlockReader.Read(html), 1),
                ContainerBlock nested => new DetailsFolder.Item(child, null, 1 + ProcessContainer(nested, depth + 1)),
                LeafBlock leaf        => new DetailsFolder.Item(child, null, 1 + InlinePass.Process(leaf, depth + 1)),
                _                     => new DetailsFolder.Item(child, null, 1),
            });
        }
        return DetailsFolder.Fold(container, depth, items);
    }
```

`HtmlBlock` is a `LeafBlock`, so its arm must come first. The container is only rebuilt after its own enumeration has finished.

In `test/Capacitor.App.Tests.Unit/GitHubHtml/TreeDump.cs`, add before the `ParagraphBlock` case:

```csharp
            case HtmlPreBlock pre:        Leaf(builder, "pre", pre); break;
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/HtmlBlockRuleTests/*"`, then `InlinePairingTests`, `LinkHoistingTests` and `NormalisationTests` again.
Expected: PASS (23, 32, 9 and 6 tests).

- [ ] **Step 8: Record the `</pre>` line end in the spec**

In `docs/superpowers/specs/2026-09-17-ai2889-github-markdown-pr-reader-design.md`, D5, the bullet that begins "Whitespace is kept." — after "The line end directly after `<pre>` is dropped, as HTML does." add: "So is the one directly before `</pre>`: browsers do not show it as a line."

- [ ] **Step 9: Commit**

```bash
git add src/Capacitor.App/GitHubHtml/HtmlBlockPartKind.cs src/Capacitor.App/GitHubHtml/HtmlBlockPart.cs src/Capacitor.App/GitHubHtml/HtmlBlockPlan.cs src/Capacitor.App/GitHubHtml/HtmlBlockReader.cs src/Capacitor.App/GitHubHtml/InlineBuildMode.cs src/Capacitor.App/GitHubHtml/InlineBuilder.cs src/Capacitor.App/GitHubHtml/HtmlPreBlock.cs src/Capacitor.App/GitHubHtml/DetailsFolder.cs src/Capacitor.App/GitHubHtml/GitHubHtmlPass.cs test/Capacitor.App.Tests.Unit/GitHubHtml/TreeDump.cs test/Capacitor.App.Tests.Unit/GitHubHtml/HtmlBlockRuleTests.cs docs/superpowers/specs/2026-09-17-ai2889-github-markdown-pr-reader-design.md
git commit -m "Convert HTML blocks into paragraphs and pre blocks, all or nothing (#982)" -m "A block converts whole or stays source, so a half-converted comment never appears. Comments go token by token: a comment-type block keeps the visible text of its closing line." -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 7: `<details>` — matching, rejection, folding

`details` tags pair across sibling blocks. Matching reads every HTML block of a container, rejected ones included, so eligibility never changes who pairs. Rejection then propagates — a rejected pair rejects both its blocks, a rejected block rejects the partner blocks of all its pairs — to a fixpoint, before anything mutates. A surviving pair folds everything between its tags into a `DetailsBlock`.

**Files:**
- Create: `src/Capacitor.App/GitHubHtml/DetailsBlock.cs`
- Modify: `src/Capacitor.App/GitHubHtml/DetailsFolder.cs` (replace the whole file), `GitHubHtmlPass.cs` (ordinals)
- Modify: `test/Capacitor.App.Tests.Unit/GitHubHtml/TreeDump.cs` (add the `DetailsBlock` case)
- Test: `test/Capacitor.App.Tests.Unit/GitHubHtml/DetailsFoldTests.cs`

**Interfaces:**
- Consumes: `HtmlBlockPlan.DetailsTags` / `Parts`, `InlineBuilder.Build`, `GitHubHtmlPass.MaxDetailsNesting` / `MaxDepth`.
- Produces: `DetailsBlock : ContainerBlock { ContainerInline Summary; bool IsOpen; int Ordinal }`; `DetailsFolder.Fold` keeps its signature.

- [ ] **Step 1: Write the failing tests**

Add to `test/Capacitor.App.Tests.Unit/GitHubHtml/TreeDump.cs`, before the `HtmlPreBlock` case:

```csharp
            case DetailsBlock details:
                builder.Append(details.IsOpen ? "details+" : "details").Append('#').Append(details.Ordinal).Append('{');
                Inlines(builder, details.Summary);
                builder.Append('}');
                Blocks(builder, "", details);
                break;
```

A details section prints as `details#0{summary}(children)`, `details+` when `open`.

`test/Capacitor.App.Tests.Unit/GitHubHtml/DetailsFoldTests.cs`:

```csharp
using Capacitor.App.GitHubHtml;

namespace Capacitor.App.Tests.Unit.GitHubHtml;

public class DetailsFoldTests {
    const string BotComment = "<details>\n<summary><strong>Agent Prompt</strong></summary>\n\nSome *markdown* here.\n\n</details>";

    static int Count(string text, string piece) => text.Split(piece).Length - 1;

    [Test]
    public async Task Markdown_between_blank_lines_folds_into_the_details() {
        await Assert.That(Trees.Shape(BotComment)).IsEqualTo("doc(html('<details>\\n<summary><strong>Agent Prompt</strong></summary>'),p('Some ',em*1('markdown'),' here.'),html('</details>'))");
        await Assert.That(Trees.Dump(BotComment)).IsEqualTo("doc(details#0{em*2('Agent Prompt')}(p('Some ',em*1('markdown'),' here.')))");
    }

    /// Inside one block the content is HTML, not markdown, as on github.com.
    [Test]
    public async Task A_pair_inside_one_block_folds_too() =>
        await Assert.That(Trees.Dump("<details><summary>S</summary>text *x* <b>y</b></details>")).IsEqualTo("doc(details#0{'S'}(p('text *x* ',em*2('y'))))");

    [Test]
    public async Task Open_and_missing_summary_are_carried() {
        await Assert.That(Trees.Dump("<details open>\n<summary>S</summary>\n\nbody\n\n</details>")).IsEqualTo("doc(details+#0{'S'}(p('body')))");
        await Assert.That(Trees.Dump("<details>\n\nbody\n\n</details>")).IsEqualTo("doc(details#0{}(p('body')))");
    }

    [Test]
    public async Task Content_outside_the_pair_stays_outside() =>
        await Assert.That(Trees.Dump("<details><summary>S</summary></details>after")).IsEqualTo("doc(details#0{'S'}(),p('after'))");

    [Test]
    public async Task Pairs_nest_and_ordinals_follow_document_order() {
        const string nested = "<details>\n<summary>Outer</summary>\n\n<details>\n<summary>Inner</summary>\n\ninner body\n\n</details>\n\nouter tail\n\n</details>";
        await Assert.That(Trees.Dump(nested)).IsEqualTo("doc(details#0{'Outer'}(details#1{'Inner'}(p('inner body')),p('outer tail')))");

        const string siblings = "<details>\n\na\n\n</details>\n\n<details>\n\n<details>\n\nc\n\n</details>\n\n</details>";
        await Assert.That(Trees.Dump(siblings)).IsEqualTo("doc(details#0{}(p('a')),details#1{}(details#2{}(p('c'))))");
    }

    /// Pins rejection propagation: the inner opener is rejected for its `<span>`, which rejects
    /// its partner closer with it, and the outer pair folds around both.
    [Test]
    public async Task A_rejected_opener_takes_its_closer_with_it_and_the_outer_pair_still_folds() {
        const string markdown = "<details><summary>Outer</summary>\n\n<details><span>unsupported</span>\n\ninner body\n\n</details>\n\nouter tail\n\n</details>";
        await Assert.That(Trees.Dump(markdown))
            .IsEqualTo("doc(details#0{'Outer'}(html('<details><span>unsupported</span>'),p('inner body'),html('</details>'),p('outer tail')))");
    }

    [Test]
    public async Task An_unmatched_tag_leaves_its_block_as_source() {
        await Assert.That(Trees.Dump("<details>\n<summary>S</summary>\n\nbody")).IsEqualTo("doc(html('<details>\\n<summary>S</summary>'),p('body'))");
        await Assert.That(Trees.Dump("body\n\n</details>")).IsEqualTo("doc(p('body'),html('</details>'))");
    }

    /// Matching happens within one parent: a pair split between a list item and the document is
    /// two unmatched tags.
    [Test]
    public async Task A_pair_is_matched_within_one_container_only() {
        const string split = "- <details>\n  <summary>S</summary>\n\n  item body\n\n</details>";
        await Assert.That(Trees.Dump(split)).IsEqualTo("doc(list(li(html('<details>\\n<summary>S</summary>'),p('item body'))),html('</details>'))");

        const string inItem = "- <details>\n  <summary>S</summary>\n\n  body\n\n  </details>";
        await Assert.That(Trees.Dump(inItem)).IsEqualTo("doc(list(li(details#0{'S'}(p('body')))))");

        const string inQuote = "> <details>\n> <summary>S</summary>\n>\n> body\n>\n> </details>";
        await Assert.That(Trees.Dump(inQuote)).IsEqualTo("doc(quote(details#0{'S'}(p('body'))))");
    }

    [Test]
    public async Task A_second_summary_rejects_its_block_and_the_pair() =>
        await Assert.That(Trees.Dump("<details>\n<summary>A</summary>\n<summary>B</summary>\n\n</details>"))
            .IsEqualTo("doc(html('<details>\\n<summary>A</summary>\\n<summary>B</summary>'),html('</details>'))");

    /// A block that closes one pair and opens another that never closes is rejected whole, and
    /// that rejection reaches the pair it closed.
    [Test]
    public async Task A_block_with_an_unmatched_opener_rejects_the_pair_it_closes() =>
        await Assert.That(Trees.Dump("<details>\n\na\n\n</details><details>\n\nb"))
            .IsEqualTo("doc(html('<details>'),p('a'),html('</details><details>'),p('b'))");

    [Test]
    public async Task A_summary_holds_no_links() =>
        await Assert.That(Trees.Dump("<details>\n<summary><a href=\"https://u.example\">L</a> <img src=\"https://h/i.png\"></summary>\n\nb\n\n</details>"))
            .IsEqualTo("doc(details#0{'L',' ','i.png'}(p('b')))");

    [Test]
    public async Task A_pre_directly_inside_the_pair_is_accepted() =>
        await Assert.That(Trees.Dump("<details><summary>S</summary><pre>a\nb</pre></details>")).IsEqualTo("doc(details#0{'S'}(pre('a',br,'b')))");

    [Test]
    public async Task A_lone_closer_block_passes_the_block_rule_and_folds() =>
        await Assert.That(Trees.Dump("<details>\n\n- one\n- two\n\n</details>")).IsEqualTo("doc(details#0{}(list(li(p('one')),li(p('two')))))");

    /// The ninth level is not folded; the eight around it are.
    [Test]
    public async Task Nesting_deeper_than_eight_stays_source() {
        var markdown = string.Concat(Enumerable.Repeat("<details>\n\n", 9)) + "x\n\n" + string.Concat(Enumerable.Repeat("</details>\n\n", 9));
        var dump = Trees.Dump(markdown);
        await Assert.That(Count(dump, "details#")).IsEqualTo(8);
        await Assert.That(dump).Contains("html('<details>'),p('x'),html('</details>')");
    }

    /// Pins the fold's depth budget: the details node sits one level below its container, its
    /// paragraph two, that paragraph's root three and the literal four.
    [Test]
    public async Task A_fold_that_would_pass_the_depth_limit_is_refused() {
        static string Quoted(int levels, string line) => string.Concat(Enumerable.Repeat("> ", levels)) + line;
        static string Fixture(int levels) => string.Join('\n', [Quoted(levels, "<details>"), Quoted(levels, ""), Quoted(levels, "x"), Quoted(levels, ""), Quoted(levels, "</details>")]);

        var accepted = Trees.Parse(Fixture(96));
        await Assert.That(TreeDump.Of(accepted)).Contains("details#0{}(p('x'))");
        await Assert.That(Trees.MaxDepth(accepted)).IsEqualTo(GitHubHtmlPass.MaxDepth);

        var refused = Trees.Parse(Fixture(97));
        await Assert.That(TreeDump.Of(refused)).Contains("html('<details>'),p('x'),html('</details>')");
        await Assert.That(Trees.MaxDepth(refused)).IsEqualTo(GitHubHtmlPass.MaxDepth);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/DetailsFoldTests/*"`
Expected: build FAILS — `DetailsBlock` does not exist.

- [ ] **Step 3: Write `DetailsBlock.cs`**

```csharp
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Capacitor.App.GitHubHtml;

/// A `details` element: its summary, whether it starts open, and its position among the
/// document's details sections, which a view keys its expanded state by.
public sealed class DetailsBlock : ContainerBlock {
    public DetailsBlock() : base(null) { }

    public ContainerInline Summary { get; set; } = new();

    public bool IsOpen { get; set; }

    public int Ordinal { get; set; }
}
```

- [ ] **Step 4: Replace `DetailsFolder.cs`**

```csharp
using Markdig.Syntax;

namespace Capacitor.App.GitHubHtml;

/// Rebuilds one block container from its children and the plans of its HTML blocks. Matching,
/// rejection and the depth check all run before anything mutates; the two walks share their
/// arithmetic, the first deciding what fits and the second building it.
static class DetailsFolder {
    public sealed class Item(Block block, HtmlBlockPlan? plan, int reach) {
        public Block Block => block;
        public HtmlBlockPlan? Plan => plan;
        /// The levels the block occupies if it stays as it is.
        public int Reach => reach;
        /// One entry per `details` tag of the block, aligned with the plan's `DetailsTags`.
        public List<Pair?> TagPairs { get; } = [];
        public bool Converts => plan is { Rejected: false };
    }

    public sealed class Pair(Item open, Item close) {
        public Item Open => open;
        public Item Close => close;
        public int Level { get; set; }
        public bool Rejected { get; set; }
        public Item Partner(Item item) => ReferenceEquals(item, open) ? close : open;
    }

    sealed class Frame(Pair pair, DetailsBlock? node) {
        public Pair Pair => pair;
        public DetailsBlock? Node => node;
        /// The levels the details node occupies, itself included.
        public int Reach { get; set; } = 1;
    }

    /// Returns the reach of the container's children.
    public static int Fold(ContainerBlock container, int depth, List<Item> items) {
        if (!items.Exists(item => item.Plan is not null)) return Reach(items);
        var pairs = Match(items);
        Propagate(items, pairs);
        Walk(container, depth, items, apply: false);
        Propagate(items, pairs);
        if (!items.Exists(item => item.Converts)) return Reach(items);
        return Walk(container, depth, items, apply: true);
    }

    static int Reach(List<Item> items) {
        var reach = 0;
        foreach (var item in items) reach = Math.Max(reach, item.Reach);
        return reach;
    }

    /// Pairs every `details` open tag with its close tag across the container's HTML blocks,
    /// rejected ones included, so a rejected block cannot change who pairs with whom.
    static List<Pair> Match(List<Item> items) {
        var pairs = new List<Pair>();
        var open = new Stack<(Item Item, int Tag)>();
        foreach (var item in items) {
            if (item.Plan is not { } plan) continue;
            for (var tag = 0; tag < plan.DetailsTags.Count; tag++) {
                item.TagPairs.Add(null);
                if (plan.DetailsTags[tag]) { open.Push((item, tag)); continue; }
                if (open.Count == 0) { plan.Rejected = true; continue; }
                var (opener, openerTag) = open.Pop();
                var pair = new Pair(opener, item);
                opener.TagPairs[openerTag] = pair;
                item.TagPairs[tag] = pair;
                pairs.Add(pair);
            }
        }
        foreach (var (item, _) in open) item.Plan!.Rejected = true;

        // Levels count matched pairs only: an open tag that never closes nests nothing.
        var level = 0;
        foreach (var item in items) {
            if (item.Plan is not { } plan) continue;
            for (var tag = 0; tag < item.TagPairs.Count; tag++) {
                if (item.TagPairs[tag] is not { } pair) continue;
                if (!plan.DetailsTags[tag]) { level--; continue; }
                pair.Level = ++level;
                if (level > GitHubHtmlPass.MaxDetailsNesting) pair.Rejected = true;
            }
        }
        return pairs;
    }

    /// A rejected pair rejects both its blocks; a rejected block rejects the partner block of
    /// every pair it takes part in. Each block is rejected once, so this is linear in the tags.
    static void Propagate(List<Item> items, List<Pair> pairs) {
        foreach (var pair in pairs) {
            if (!pair.Rejected) continue;
            pair.Open.Plan!.Rejected = true;
            pair.Close.Plan!.Rejected = true;
        }
        var pending = new Queue<Item>(items.Where(item => item.Plan is { Rejected: true }));
        while (pending.TryDequeue(out var item)) {
            foreach (var pair in item.TagPairs) {
                if (pair is null || pair.Rejected) continue;
                pair.Rejected = true;
                var partner = pair.Partner(item);
                if (partner.Plan!.Rejected) continue;
                partner.Plan.Rejected = true;
                pending.Enqueue(partner);
            }
        }
    }

    /// Walks the items as they will be laid out. With `apply` false it only measures, rejecting a
    /// pair or a block whose deepest node would pass the limit; with `apply` true it builds.
    static int Walk(ContainerBlock container, int depth, List<Item> items, bool apply) {
        var frames = new Stack<Frame>();
        var reach = 0;
        if (apply) container.Clear();

        foreach (var item in items) {
            if (!item.Converts) {
                if (apply) Target().Add(item.Block);
                Note(item.Reach);
                continue;
            }
            var tag = 0;
            foreach (var part in item.Plan!.Parts) {
                switch (part.Kind) {
                    case HtmlBlockPartKind.DetailsOpen: {
                        var node = apply ? new DetailsBlock { IsOpen = part.IsOpen } : null;
                        if (node is not null) Target().Add(node);
                        frames.Push(new Frame(item.TagPairs[tag++]!, node));
                        break;
                    }
                    case HtmlBlockPartKind.DetailsClose: {
                        tag++;
                        var frame = frames.Pop();
                        // The node sits at depth + 1 + the frames still open around it.
                        if (!apply && depth + frames.Count + frame.Reach > GitHubHtmlPass.MaxDepth) frame.Pair.Rejected = true;
                        Note(frame.Reach);
                        break;
                    }
                    case HtmlBlockPartKind.Summary: {
                        var frame = frames.Peek();
                        if (apply) frame.Node!.Summary = InlineBuilder.Build(part.Tokens, InlineBuildMode.Summary);
                        frame.Reach = Math.Max(frame.Reach, 1 + part.Reach);
                        break;
                    }
                    default: {
                        if (!apply && depth + frames.Count + part.Reach > GitHubHtmlPass.MaxDepth) { item.Plan.Rejected = true; break; }
                        if (apply) Target().Add(Build(part));
                        Note(part.Reach);
                        break;
                    }
                }
            }
        }
        return reach;

        ContainerBlock Target() => frames.Count > 0 ? frames.Peek().Node! : container;

        void Note(int levels) {
            if (frames.Count > 0) frames.Peek().Reach = Math.Max(frames.Peek().Reach, 1 + levels);
            else reach = Math.Max(reach, levels);
        }
    }

    static Block Build(HtmlBlockPart part) => part.Kind == HtmlBlockPartKind.Pre
        ? new HtmlPreBlock(InlineBuilder.Build(part.Tokens, InlineBuildMode.Pre))
        : new ParagraphBlock { Inline = InlineBuilder.Build(part.Tokens, InlineBuildMode.Paragraph) };
}
```

Why one measuring walk is enough: every rejection makes the tree shallower — a rejected pair drops its level, a rejected block becomes a source leaf — so the failures the first walk finds are a superset of what the final layout needs, and the second `Propagate` only removes more. That makes the check conservative near the limit and never unsafe.

- [ ] **Step 5: Assign ordinals**

In `src/Capacitor.App/GitHubHtml/GitHubHtmlPass.cs`, replace `Run`:

```csharp
    public static void Run(MarkdownDocument document) {
        ProcessContainer(document, 0);
        var ordinal = 0;
        foreach (var details in document.Descendants<DetailsBlock>()) details.Ordinal = ordinal++;
    }
```

`Descendants<T>` walks in document order.

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/DetailsFoldTests/*"`, then `HtmlBlockRuleTests`, `InlinePairingTests`, `LinkHoistingTests`, `NormalisationTests`.
Expected: PASS (15, 23, 32, 9 and 6 tests).

- [ ] **Step 7: Commit**

```bash
git add src/Capacitor.App/GitHubHtml/DetailsBlock.cs src/Capacitor.App/GitHubHtml/DetailsFolder.cs src/Capacitor.App/GitHubHtml/GitHubHtmlPass.cs test/Capacitor.App.Tests.Unit/GitHubHtml/TreeDump.cs test/Capacitor.App.Tests.Unit/GitHubHtml/DetailsFoldTests.cs
git commit -m "Fold details pairs across sibling blocks into a details node (#982)" -m "Matching reads every HTML block, rejected ones included, so eligibility never changes who pairs; rejection then propagates through partner blocks before anything mutates." -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 8: Totality and work bounds

The pass runs on the UI thread over text a stranger wrote. This task pins that it neither throws nor leaves a node past the depth limit on generated tag soup, and that a large document with many details pairs is processed in linear time.

**Files:**
- Test: `test/Capacitor.App.Tests.Unit/GitHubHtml/PassTotalityTests.cs`

**Interfaces:**
- Consumes: `GitHubHtmlPass.Run` directly, so that an exception reaches the test rather than the extension's catch; `Trees.ParseWithoutPass`, `Trees.MaxDepth`.

- [ ] **Step 1: Write the tests**

`test/Capacitor.App.Tests.Unit/GitHubHtml/PassTotalityTests.cs`:

```csharp
using System.Text;
using Capacitor.App.GitHubHtml;

namespace Capacitor.App.Tests.Unit.GitHubHtml;

public class PassTotalityTests {
    static readonly string[] Pieces = [
        "<b>", "</b>", "<i>", "</i>", "<code>", "</code>", "<a href=\"https://u.example\">", "<a href=\"javascript:x\">", "<a>", "</a>",
        "<img src=\"https://h/i.png\" alt=\"i\">", "<img src=\"https://h/a%0Ab.png\">", "<br>", "<br/>", "</br>",
        "<details>", "<details open>", "</details>", "<summary>", "</summary>", "<pre>", "</pre>", "<pre><code>", "</code></pre>",
        "<span>", "</span>", "<div>", "</div>", "<a href=\"", "<!-- c -->", "<!-- open", "<b\n>", "<b/>",
        "text", "more text", " ", "\n", "\n\n", "&amp;", "&#x27;", "&#10;", "**bold**", "*em*", "`code`", "![alt](https://h/x.png)", "![](https://h/y.png)",
        "<https://example.com>", "[l](https://u.example)", "https://bare.example/x", "> ", "- ", "\n> ", "\n- ", "| a | b |\n|---|---|\n| c | d |\n",
    ];

    /// Deterministic tag soup: random interleavings of inline, block-only, unknown, unclosed and
    /// truncated tags with markdown. Quote and list markers stay shallow so the parsed tree itself
    /// does not exceed the pass's own limit.
    static string Soup(int seed) {
        var random = new Random(seed);
        var builder = new StringBuilder();
        var length = random.Next(1, 400);
        for (var i = 0; i < length; i++) builder.Append(Pieces[random.Next(Pieces.Length)]);
        return builder.ToString();
    }

    /// Calls the pass directly: the extension's boundary catch would hide a throw.
    [Test]
    public async Task Generated_tag_soup_never_throws_and_never_passes_the_depth_limit() {
        for (var seed = 0; seed < 500; seed++) {
            var document = Trees.ParseWithoutPass(Soup(seed));
            GitHubHtmlPass.Run(document);
            await Assert.That(Trees.MaxDepth(document)).IsLessThanOrEqualTo(GitHubHtmlPass.MaxDepth).Because($"seed {seed}");
        }
    }

    [Test]
    public async Task Every_source_tag_left_behind_is_still_a_valid_tree_node() {
        for (var seed = 0; seed < 200; seed++) {
            var document = Trees.Parse(Soup(seed));
            // Dumping walks the whole tree and touches every node's parent links.
            await Assert.That(TreeDump.Of(document)).StartsWith("doc(");
        }
    }

    /// Pins linear work across blocks: thousands of details pairs, each with markdown between,
    /// and thousands of paragraphs of inline tags.
    [Test]
    [Timeout(30_000)]
    public async Task Thousands_of_details_pairs_and_paragraphs_process_in_time(CancellationToken _) {
        var builder = new StringBuilder();
        for (var i = 0; i < 5_000; i++) builder.Append("<details>\n<summary>S").Append(i).Append("</summary>\n\nbody <b>").Append(i).Append("</b> <a href=\"https://u.example/").Append(i).Append("\">l</a>\n\n</details>\n\n");
        var document = Trees.Parse(builder.ToString());
        await Assert.That(TreeDump.Of(document).Split("details#").Length - 1).IsEqualTo(5_000);
    }

    /// Five thousand openers then five thousand closers all match, nested; the cap rejects every
    /// level past the eighth, and propagation must stay linear in the tags.
    [Test]
    [Timeout(30_000)]
    public async Task Thousands_of_nested_details_tags_process_in_time(CancellationToken _) {
        var builder = new StringBuilder();
        for (var i = 0; i < 5_000; i++) builder.Append("<details>\n\n");
        for (var i = 0; i < 5_000; i++) builder.Append("</details>\n\n");
        var document = Trees.Parse(builder.ToString());
        await Assert.That(Trees.MaxDepth(document)).IsLessThanOrEqualTo(GitHubHtmlPass.MaxDepth);
    }
}
```

- [ ] **Step 2: Run the tests**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/PassTotalityTests/*"`
Expected: PASS, 4 tests. A failure here is a defect in Tasks 3–7: the seed in the message reproduces it — print `Soup(seed)` and `Trees.Shape` of it, find the rule the shape breaks, fix the rule and add the shape to that rule's test class.

- [ ] **Step 3: Commit**

```bash
git add test/Capacitor.App.Tests.Unit/GitHubHtml/PassTotalityTests.cs
git commit -m "Pin the HTML pass as total and linear on generated tag soup (#982)" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 9: Rendering — the flavor, source text, one hyperlink at a time, images and `<pre>`

`MarkdownView` gains a `Flavor`. The GitHub flavor selects the pipeline of Task 3 and a GitHub-mode `KcapMarkdownExtension`, whose link renderer draws an image as a link labelled with its alt text and never nests one hyperlink in another. Source text goes out as runs split at line ends, in both flavors.

**Files:**
- Create: `src/Capacitor.App/Views/MarkdownFlavor.cs`, `SourceText.cs`, `LinkScope.cs`, `HtmlPreBlockRenderer.cs`
- Modify: `src/Capacitor.App/Views/KcapMarkdownExtension.cs` (replace), `MarkdownView.cs` (replace), `MarkdownStyles.axaml` (append)
- Create: `test/Capacitor.App.Tests.Unit/MarkdownViewHarness.cs`, `GitHubMarkdownViewTests.cs`
- Modify: `test/Capacitor.App.Tests.Unit/MarkdownViewTests.cs` (helpers move to the harness)

**Interfaces:**
- Consumes: `GitHubPipeline.Instance`, `HtmlPreBlock`, `ImageLabel.For`, `LinkPolicy.IsOpenable`.
- Produces:
  - `MarkdownFlavor { Chat, GitHub }`; `MarkdownView.Flavor` (styled property, default `Chat`).
  - `KcapMarkdownExtension.Chat` / `.GitHub` (shared instances; the parameterless constructor goes away).
  - `LinkScope { bool Inside; Enter(); Exit() }`; `SourceText.Write(AvaloniaRenderer, string)`.
  - Test harness `MarkdownViewHarness`: `Show(markdown, flavor = Chat, width = 400) : (Window, MarkdownView, List<string> Opened)`, `All<T>`, `Paragraphs`, `Reads`, `Spans<T>`, `Links`, `AllLinks : IEnumerable<(TextBlock Block, MarkdownHyperlink Link)>`, `Runs`, `StartOf(TextBlock, MarkdownHyperlink) : int`, `ClickAt(Window, TextBlock, int)`, `Click(Window, TextBlock, MarkdownHyperlink)`, `Viewer(Visual) : MarkdownViewer`.

- [ ] **Step 1: Move the view-test helpers into a harness**

`test/Capacitor.App.Tests.Unit/MarkdownViewHarness.cs`:

```csharp
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.Views;
using MarkView.Avalonia;
using MarkView.Avalonia.Rendering.Inlines;
using ReactiveUI.Reactive;

namespace Capacitor.App.Tests.Unit;

/// Shows a `MarkdownView` in a headless window and reads what it rendered. Every caller runs
/// inside `AvaloniaSession.RunOnUiAsync` and carries `[NotInParallel("AvaloniaSession")]`.
internal static class MarkdownViewHarness {
    public static (Window Window, MarkdownView View, List<string> Opened) Show(string markdown, MarkdownFlavor flavor = MarkdownFlavor.Chat, double width = 400) {
        var opened = new List<string>();
        ICommand open = ReactiveCommand.Create<string>(opened.Add);
        var view = new MarkdownView { Flavor = flavor, Text = markdown, OpenLink = open, Width = width };
        var window = new Window { Content = view, Width = width + 100, Height = 400 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        return (window, view, opened);
    }

    public static IEnumerable<T> All<T>(Visual root) where T : Visual => root.GetVisualDescendants().OfType<T>();

    public static MarkdownViewer Viewer(Visual root) => All<MarkdownViewer>(root).Single();

    public static IEnumerable<TextBlock> Paragraphs(Visual root) => All<TextBlock>(root).Where(t => t.Classes.Contains("markdown-paragraph"));

    /// A text block built from inlines leaves Text null and carries its characters on the
    /// inline collection, so a "what does this block read as" assertion has to consult both.
    public static string Reads(TextBlock block) => block.Text ?? block.Inlines?.Text ?? "";

    public static IEnumerable<T> Spans<T>(InlineCollection inlines) where T : Inline {
        foreach (var inline in inlines) {
            if (inline is T t) yield return t;
            if (inline is Span span) foreach (var nested in Spans<T>(span.Inlines)) yield return nested;
        }
    }

    public static IEnumerable<MarkdownHyperlink> Links(Visual root) => Paragraphs(root).SelectMany(p => Spans<MarkdownHyperlink>(p.Inlines!));

    public static IEnumerable<(TextBlock Block, MarkdownHyperlink Link)> AllLinks(Visual root) =>
        All<TextBlock>(root).Where(t => t.Inlines is not null).SelectMany(t => Spans<MarkdownHyperlink>(t.Inlines!).Select(link => (t, link)));

    public static IEnumerable<Run> Runs(Visual root) => All<TextBlock>(root).Where(t => t.Inlines is not null).SelectMany(t => Spans<Run>(t.Inlines!));

    /// The index of the hyperlink's first character in its block, measured as MarkView measures
    /// when it resolves a press: a run its length, a line break the newline's length, else one.
    public static int StartOf(TextBlock block, MarkdownHyperlink link) {
        var found = -1;
        Walk(block.Inlines!, link, ref found, 0);
        return found;
    }

    static int Walk(InlineCollection inlines, MarkdownHyperlink target, ref int found, int offset) {
        foreach (var inline in inlines) {
            if (ReferenceEquals(inline, target)) { found = offset; return offset; }
            switch (inline) {
                case Run run: offset += run.Text?.Length ?? 0; break;
                case LineBreak: offset += Environment.NewLine.Length; break;
                case Span span:
                    offset = Walk(span.Inlines, target, ref found, offset);
                    if (found >= 0) return offset;
                    break;
                default: offset += 1; break;
            }
        }
        return offset;
    }

    /// A press just inside the leading edge of the glyph at `index`, the way MarkView's caret-based
    /// dispatch expects one.
    public static void ClickAt(Window window, TextBlock block, int index) {
        var glyph = block.TextLayout.HitTestTextPosition(index);
        var point = block.TranslatePoint(new Point(glyph.X + block.Padding.Left + 2, glyph.Y + block.Padding.Top + glyph.Height / 2), window)!.Value;
        window.MouseMove(point);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    public static void Click(Window window, TextBlock block, MarkdownHyperlink link) => ClickAt(window, block, StartOf(block, link));
}
```

In `test/Capacitor.App.Tests.Unit/MarkdownViewTests.cs`: delete the private `Show`, `All`, `Paragraphs`, `Reads`, `Spans` and `Links` members, add `using static Capacitor.App.Tests.Unit.MarkdownViewHarness;`, and drop the `using` lines the file no longer needs (`System.Windows.Input`, `ReactiveUI.Reactive`, `Avalonia.VisualTree`, `Avalonia.Controls.Documents` stays for `Bold`/`Run`). The click in `An_allowed_link_opens_through_the_command_on_click` becomes `ClickAt(window, paragraph, "See ".Length);`. Every test body stays as it is.

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/MarkdownViewTests/*"`
Expected: PASS, 10 tests — the same ten as before the move.

- [ ] **Step 2: Write the failing tests**

`test/Capacitor.App.Tests.Unit/GitHubMarkdownViewTests.cs`:

```csharp
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Threading;
using Capacitor.App.Views;
using MarkView.Avalonia.Rendering;
using static Capacitor.App.Tests.Unit.AvaloniaSession;
using static Capacitor.App.Tests.Unit.MarkdownViewHarness;

namespace Capacitor.App.Tests.Unit;

public class GitHubMarkdownViewTests {
    const string BotComment =
        "<img src=\"https://img.shields.io/badge/Medium-634FD1\" height=\"20px\" alt=\"Remediation recommended\">\n\n" +
        "1. The title breaks casing <code>📘 Rule violation</code>\n\n" +
        "<pre>\nThe supplied title <b><i>Name the repo&#x27;s projects</i></b> begins its\nimperative clause with a capital.\n</pre>";

    static string Quoted(int levels, string text) => string.Concat(Enumerable.Repeat("> ", levels)) + text;

    /// Pins the headline: the bot comment renders with no literal tag anywhere, the badge as a
    /// link labelled by its alt text, the emphasis inside the pre, and the code span.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_bot_comment_renders_with_no_literal_tags() {
        await RunOnUiAsync(async () => {
            var (window, root, _) = Show(BotComment, MarkdownFlavor.GitHub);
            try {
                await Assert.That(All<TextBlock>(root).Select(Reads).Any(text => text.Contains('<'))).IsFalse();
                var badge = AllLinks(root).Single();
                await Assert.That(badge.Link.NavigateUri?.ToString()).IsEqualTo("https://img.shields.io/badge/Medium-634FD1");
                await Assert.That(badge.Link.Inlines.Text).IsEqualTo("Remediation recommended");
                await Assert.That(Runs(root).Any(r => r.Text == "📘 Rule violation" && r.Classes.Contains("markdown-code-inline"))).IsTrue();

                var pre = All<Border>(root).Single(b => b.Classes.Contains("markdown-pre"));
                await Assert.That(pre.Classes.Contains("markdown-code-block")).IsTrue();
                var text = All<MarkdownSelectableTextBlock>(pre).Single();
                var bold = Spans<Bold>(text.Inlines!).Single();
                await Assert.That(Spans<Italic>(bold.Inlines).Single().Inlines.Text).IsEqualTo("Name the repo's projects");
                await Assert.That(text.Inlines!.OfType<LineBreak>().Count()).IsEqualTo(1);
                await Assert.That(All<Image>(root)).IsEmpty();
                await Assert.That(All<TextBlock>(root).Any(t => t.Inlines?.OfType<InlineUIContainer>().Any() == true)).IsFalse();
            } finally { window.Close(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_chat_flavor_still_shows_the_same_comment_as_source() {
        await RunOnUiAsync(async () => {
            var (window, root, _) = Show(BotComment);
            try {
                var texts = All<TextBlock>(root).Select(Reads).ToList();
                await Assert.That(texts.Any(t => t.Contains("<img src="))).IsTrue();
                await Assert.That(texts.Any(t => t.Contains("<pre>"))).IsTrue();
                await Assert.That(texts.Any(t => t.Contains("<code>📘 Rule violation</code>"))).IsTrue();
                await Assert.That(AllLinks(root)).IsEmpty();
            } finally { window.Close(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_image_is_a_link_that_opens_through_the_command() {
        await RunOnUiAsync(async () => {
            var (window, root, opened) = Show("See ![the shot](https://h/x.png) here.", MarkdownFlavor.GitHub);
            try {
                var (block, link) = AllLinks(root).Single();
                await Assert.That(link.Inlines.Text).IsEqualTo("the shot");
                Click(window, block, link);
                await Assert.That(opened).IsEquivalentTo(new[] { "https://h/x.png" });
            } finally { window.Close(); }
        });
    }

    /// Pins one hyperlink at a time, on ancestry and on the URL dispatched: the outermost openable
    /// link wins, and a refused outer link lets the inner one through.
    [Test]
    [NotInParallel("AvaloniaSession")]
    [Arguments("[![badge](https://h/b.png)](https://outer.example)", "https://outer.example", "badge")]
    [Arguments("<a href=\"https://outer.example\"><img src=\"https://h/b.png\" alt=\"badge\"></a>", "https://outer.example", "badge")]
    [Arguments("p <a href=\"https://outer.example\">[inner](https://inner.example)</a>", "https://outer.example", "inner")]
    [Arguments("p <a href=\"https://outer.example\">a <a href=\"https://inner.example\">b</a></a>", "https://outer.example", "a b")]
    [Arguments("p <a href=\"javascript:x\">[inner](https://inner.example)</a>", "https://inner.example", "inner")]
    public async Task Only_one_hyperlink_at_a_time(string markdown, string expected, string label) {
        await RunOnUiAsync(async () => {
            var (window, root, opened) = Show(markdown, MarkdownFlavor.GitHub);
            try {
                var (block, link) = AllLinks(root).Single();
                await Assert.That(link.NavigateUri?.ToString()).IsEqualTo(expected);
                await Assert.That(link.Inlines.Text).IsEqualTo(label);
                await Assert.That(Spans<MarkdownHyperlink>(link.Inlines)).IsEmpty();
                Click(window, block, link);
                await Assert.That(opened).IsEquivalentTo(new[] { expected });
            } finally { window.Close(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_chat_flavor_keeps_a_linked_image_as_source_text_inside_the_link() {
        await RunOnUiAsync(async () => {
            var (window, root, _) = Show("[![badge](https://h/b.png)](https://outer.example)");
            try {
                var link = Links(root).Single();
                await Assert.That(link.NavigateUri?.ToString()).IsEqualTo("https://outer.example");
                await Assert.That(link.Inlines.Text).IsEqualTo("![badge](https://h/b.png)");
            } finally { window.Close(); }
        });
    }

    /// Every link here sits under formatting, inside a pre, after an emoji, in a list item, or is
    /// an image — and each is a direct inline of a selectable text block that opens exactly once.
    [Test]
    [NotInParallel("AvaloniaSession")]
    [Arguments("**[x](https://u.example/1)**", "https://u.example/1")]
    [Arguments("p <b><i><a href=\"https://u.example/2\">y</a></i></b>", "https://u.example/2")]
    [Arguments("**<https://example.com>**", "https://example.com/")]
    [Arguments("**![badge](https://h/b.png)**", "https://h/b.png")]
    [Arguments("**![](https://h/name.png)**", "https://h/name.png")]
    [Arguments("<pre><a href=\"https://u.example/pre\">first line</a>\nsecond</pre>", "https://u.example/pre")]
    [Arguments("🚀 shipped [go](https://u.example/e) now", "https://u.example/e")]
    [Arguments("- [item](https://u.example/li)", "https://u.example/li")]
    public async Task A_link_anywhere_is_a_direct_inline_that_opens_once(string markdown, string expected) {
        await RunOnUiAsync(async () => {
            var (window, root, opened) = Show(markdown, MarkdownFlavor.GitHub);
            try {
                var (block, link) = AllLinks(root).Single();
                await Assert.That(block).IsTypeOf<MarkdownSelectableTextBlock>();
                await Assert.That(block.Inlines!.Contains(link)).IsTrue();
                Click(window, block, link);
                await Assert.That(opened).IsEquivalentTo(new[] { expected });
            } finally { window.Close(); }
        });
    }

    /// MarkView measures a line break as `Environment.NewLine`, and Avalonia lays it out as one
    /// character; where the two differ, a link after a line break is the library's defect, not this
    /// flavor's, so the test only speaks where they agree.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_link_after_a_line_break_inside_pre_opens() {
        if (Environment.NewLine.Length != 1) return;
        await RunOnUiAsync(async () => {
            var (window, root, opened) = Show("<pre>first\n<a href=\"https://u.example/second\">second</a></pre>", MarkdownFlavor.GitHub);
            try {
                var (block, link) = AllLinks(root).Single();
                Click(window, block, link);
                await Assert.That(opened).IsEquivalentTo(new[] { "https://u.example/second" });
            } finally { window.Close(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_emphasised_image_reads_bold_with_either_label() {
        await RunOnUiAsync(async () => {
            foreach (var (markdown, label) in new[] { ("**![badge](https://h/b.png)**", "badge"), ("**![](https://h/name.png)**", "name.png") }) {
                var (window, root, _) = Show(markdown, MarkdownFlavor.GitHub);
                try {
                    var (_, link) = AllLinks(root).Single();
                    await Assert.That(Spans<Bold>(link.Inlines).Single().Inlines.Text).IsEqualTo(label);
                } finally { window.Close(); }
            }
        });
    }

    /// Pins the source fallback's layout guard in both flavors: a tag that spans lines lays out
    /// under a height-unconstrained parent, no run holds a line end, and the text still reads as
    /// the source.
    [Test]
    [NotInParallel("AvaloniaSession")]
    [Timeout(30_000)]
    public async Task A_multi_line_source_tag_lays_out_and_still_reads_as_source(CancellationToken _) {
        await RunOnUiAsync(async () => {
            foreach (var flavor in new[] { MarkdownFlavor.Chat, MarkdownFlavor.GitHub }) {
                var view = new MarkdownView { Flavor = flavor, Text = "a <span\n title=\"x\">b</span> c\n\nd <b\n>z", Width = 400 };
                var window = new Window { Content = new ScrollViewer { Content = new StackPanel { Children = { view } } }, Width = 500, Height = 400 };
                window.Show();
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                try {
                    await Assert.That(view.Bounds.Height).IsGreaterThan(0);
                    await Assert.That(Runs(view).Any(r => r.Text!.Contains('\n') || r.Text!.Contains('\r'))).IsFalse();
                    var texts = Paragraphs(view).Select(p => Reads(p).ReplaceLineEndings("\n")).ToList();
                    await Assert.That(texts).Contains("a <span\n title=\"x\">b</span> c");
                    await Assert.That(texts).Contains("d <b\n>z");
                } finally { window.Close(); }
            }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    [Timeout(30_000)]
    public async Task An_image_whose_file_name_unescapes_to_a_line_end_lays_out(CancellationToken _) {
        await RunOnUiAsync(async () => {
            var view = new MarkdownView { Flavor = MarkdownFlavor.GitHub, Text = "![](https://h/a%0Ab.png)\n\n[![](https://h/a%0D%0Ab.png)](https://outer.example)", Width = 400 };
            var window = new Window { Content = new ScrollViewer { Content = new StackPanel { Children = { view } } }, Width = 500, Height = 400 };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            try {
                await Assert.That(view.Bounds.Height).IsGreaterThan(0);
                await Assert.That(Runs(view).Any(r => r.Text!.Contains('\n') || r.Text!.Contains('\r'))).IsFalse();
                await Assert.That(AllLinks(view).Select(l => l.Link.Inlines.Text)).IsEquivalentTo(["a b.png", "a b.png"]);
            } finally { window.Close(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_alert_block_takes_the_app_brushes() {
        await RunOnUiAsync(async () => {
            var (window, root, _) = Show("> [!NOTE]\n> Mind the gap.", MarkdownFlavor.GitHub);
            try {
                var alert = All<Border>(root).Single(b => b.Classes.Contains("markdown-alert"));
                await Assert.That(alert.Classes.Contains("markdown-alert-note")).IsTrue();
                await Assert.That(alert.BorderBrush).IsEqualTo((IBrush)Application.Current!.FindResource("KcapInfoBrush")!);
                await Assert.That(All<TextBlock>(alert).Select(Reads)).Contains("NOTE");
                await Assert.That(Paragraphs(alert).Select(Reads)).Contains("Mind the gap.");
            } finally { window.Close(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_chat_flavor_shows_comments_images_and_details_as_source() {
        await RunOnUiAsync(async () => {
            var (window, root, _) = Show("<!-- c -->\n\n![alt](https://h/x.png)\n\n<details>\n<summary>S</summary>\n\nbody\n\n</details>");
            try {
                var texts = All<TextBlock>(root).Select(Reads).ToList();
                await Assert.That(texts).Contains("<!-- c -->");
                await Assert.That(texts).Contains("![alt](https://h/x.png)");
                await Assert.That(texts.Any(t => t.StartsWith("<details>"))).IsTrue();
                await Assert.That(All<Button>(root)).IsEmpty();
            } finally { window.Close(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Changing_the_flavor_re_renders_the_current_text() {
        await RunOnUiAsync(async () => {
            var (window, view, _) = Show("p <b>x</b>");
            try {
                await Assert.That(Paragraphs(view).Select(Reads)).Contains("p <b>x</b>");
                view.Flavor = MarkdownFlavor.GitHub;
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                await Assert.That(Spans<Bold>(Paragraphs(view).Single().Inlines!).Single().Inlines.Text).IsEqualTo("x");
                view.Flavor = MarkdownFlavor.Chat;
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                await Assert.That(Paragraphs(view).Select(Reads)).Contains("p <b>x</b>");
            } finally { window.Close(); }
        });
    }

    /// Pins the pre's shape against MarkView's list-item selection walker, which does not index
    /// the library's own code-block shape.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_pre_inside_a_list_item_is_selectable() {
        await RunOnUiAsync(async () => {
            var (window, root, _) = Show("- item\n\n  <pre>\n  alpha\n  beta\n  </pre>", MarkdownFlavor.GitHub);
            try {
                var viewer = Viewer(root);
                viewer.SelectAll();
                var selected = viewer.GetSelectedText();
                await Assert.That(selected).Contains("alpha");
                await Assert.That(selected).Contains("beta");
            } finally { window.Close(); }
        });
    }

    /// The refused normalisation shapes: deep under block quotes an autolink stays an autolink and
    /// a childless image keeps its label, and each still reads and, where direct, still opens.
    [Test]
    [NotInParallel("AvaloniaSession")]
    [Timeout(60_000)]
    public async Task Refused_normalisation_still_reads_and_opens(CancellationToken _) {
        await RunOnUiAsync(async () => {
            var (window, root, opened) = Show(Quoted(97, "<https://example.com> ![](https://h/name.png)"), MarkdownFlavor.GitHub, width: 3800);
            try {
                var texts = All<TextBlock>(root).Select(Reads).ToList();
                await Assert.That(texts.Any(t => t.Contains("https://example.com"))).IsTrue();
                await Assert.That(texts.Any(t => t.Contains("name.png"))).IsTrue();
                var (block, link) = AllLinks(root).Single(l => l.Link.NavigateUri?.ToString() == "https://example.com/");
                Click(window, block, link);
                await Assert.That(opened).IsEquivalentTo(new[] { "https://example.com/" });
            } finally { window.Close(); }

            var (boldWindow, boldRoot, _) = Show(Quoted(96, "**![](https://h/name.png)**"), MarkdownFlavor.GitHub, width: 3800);
            try {
                var bold = Paragraphs(boldRoot).SelectMany(p => Spans<Bold>(p.Inlines!)).Single();
                await Assert.That(bold.Inlines.Text).IsEqualTo("name.png");
            } finally { boldWindow.Close(); }
        });
    }
}
```

`Uri.ToString()` on `https://example.com` yields a trailing slash, which is why those expectations carry one.

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/GitHubMarkdownViewTests/*"`
Expected: build FAILS — `MarkdownFlavor` does not exist.

- [ ] **Step 4: Write the view types**

`src/Capacitor.App/Views/MarkdownFlavor.cs`:

```csharp
namespace Capacitor.App.Views;

/// Chat shows HTML as source: a transcript is not written for github.com. The reader converts it.
public enum MarkdownFlavor { Chat, GitHub }
```

`src/Capacitor.App/Views/SourceText.cs`:

```csharp
using Avalonia.Controls.Documents;
using MarkView.Avalonia.Rendering;

namespace Capacitor.App.Views;

/// Writes text as runs split at line ends. A line end inside a run never finishes laying out
/// under a height-unconstrained parent.
static class SourceText {
    public static void Write(AvaloniaRenderer renderer, string text) {
        var first = true;
        foreach (var line in text.ReplaceLineEndings("\n").Split('\n')) {
            if (!first) renderer.WriteInline(new LineBreak());
            first = false;
            if (line.Length > 0) renderer.WriteInline(new Run(line));
        }
    }
}
```

`src/Capacitor.App/Views/LinkScope.cs`:

```csharp
namespace Capacitor.App.Views;

/// One hyperlink at a time: while a hyperlink's children are being written, no link, autolink or
/// image inside them becomes another. Shared by the renderers of one render pass.
public sealed class LinkScope {
    int _depth;

    public bool Inside => _depth > 0;

    public void Enter() => _depth++;

    public void Exit() => _depth--;
}
```

`src/Capacitor.App/Views/HtmlPreBlockRenderer.cs`:

```csharp
using Avalonia.Controls;
using Avalonia.Media;
using Capacitor.App.GitHubHtml;
using MarkView.Avalonia.Rendering;

namespace Capacitor.App.Views;

/// A border around a panel around a selectable text block: the one shape both of MarkView's
/// selection walkers index, and the text block type it dispatches link clicks from.
sealed class HtmlPreBlockRenderer : AvaloniaObjectRenderer<HtmlPreBlock> {
    protected override void Write(AvaloniaRenderer renderer, HtmlPreBlock obj) {
        var text = new MarkdownSelectableTextBlock { TextWrapping = TextWrapping.Wrap };
        text.Classes.Add("markdown-pre-text");
        renderer.Push(text.Inlines!);
        renderer.WriteLeafInline(obj);
        renderer.Pop();

        var border = new Border { Child = new StackPanel { Children = { text } } };
        border.Classes.Add("markdown-code-block");
        border.Classes.Add("markdown-pre");
        renderer.WriteBlock(border);
    }
}
```

`src/Capacitor.App/Views/KcapMarkdownExtension.cs` (replace the whole file):

```csharp
using System.Text.RegularExpressions;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Capacitor.App.GitHubHtml;
using Capacitor.App.Services;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MarkView.Avalonia.Extensions;
using MarkView.Avalonia.Rendering;
using MarkView.Avalonia.Rendering.Blocks;
using MarkView.Avalonia.Rendering.Inlines;

namespace Capacitor.App.Views;

/// The app's rules over MarkView's defaults: a link exists only when the policy would open it and
/// never inside another, an image is never fetched, and HTML is shown rather than dropped.
public sealed partial class KcapMarkdownExtension(MarkdownFlavor flavor) : IMarkViewExtension {
    public static KcapMarkdownExtension Chat { get; } = new(MarkdownFlavor.Chat);

    public static KcapMarkdownExtension GitHub { get; } = new(MarkdownFlavor.GitHub);

    public void Register(AvaloniaRenderer renderer) {
        var scope = new LinkScope();
        renderer.ReplaceOrAdd<LinkInlineRenderer>(new PolicyLinkRenderer(flavor, scope));
        renderer.ReplaceOrAdd<AutolinkInlineRenderer>(new PolicyAutolinkRenderer(scope));
        renderer.ReplaceOrAdd<HtmlBlockRenderer>(new SourceHtmlBlockRenderer());
        renderer.ReplaceOrAdd<HtmlInlineRenderer>(new SourceHtmlInlineRenderer());
        if (flavor == MarkdownFlavor.GitHub) renderer.ObjectRenderers.Add(new HtmlPreBlockRenderer());
        renderer.ImageLoaders.Clear();
    }

    static MarkdownHyperlink Hyperlink(string url) {
        var link = new MarkdownHyperlink { NavigateUri = new Uri(url, UriKind.Absolute) };
        link.Classes.Add("markdown-link");
        return link;
    }

    static string Label(ContainerInline container) =>
        string.Concat(container.Select(inline => inline switch {
            LiteralInline literal   => literal.Content.ToString(),
            CodeInline code         => code.Content,
            HtmlEntityInline entity => entity.Transcoded.ToString(),
            ContainerInline nested  => Label(nested),
            _                       => "",
        }));

    sealed class PolicyLinkRenderer(MarkdownFlavor flavor, LinkScope scope) : AvaloniaObjectRenderer<LinkInline> {
        protected override void Write(AvaloniaRenderer renderer, LinkInline obj) {
            if (obj.IsImage) { Image(renderer, obj); return; }
            if (scope.Inside || !LinkPolicy.IsOpenable(obj.Url)) { renderer.WriteChildren(obj); return; }
            var link = Hyperlink(obj.Url!);
            renderer.Push(link.Inlines);
            scope.Enter();
            renderer.WriteChildren(obj);
            scope.Exit();
            renderer.Pop();
            renderer.WriteInline(link);
        }

        void Image(AvaloniaRenderer renderer, LinkInline obj) {
            if (flavor == MarkdownFlavor.Chat) { renderer.WriteInline(new Run($"![{Label(obj)}]({obj.Url})")); return; }
            if (scope.Inside || !LinkPolicy.IsOpenable(obj.Url)) { WriteLabel(renderer, obj); return; }
            var link = Hyperlink(obj.Url!);
            renderer.Push(link.Inlines);
            scope.Enter();
            WriteLabel(renderer, obj);
            scope.Exit();
            renderer.Pop();
            renderer.WriteInline(link);
        }

        /// The pass gives an image its label as a child; one it had to leave childless is
        /// labelled here, so an image always shows one.
        static void WriteLabel(AvaloniaRenderer renderer, LinkInline obj) {
            if (obj.FirstChild is null) renderer.WriteInline(new Run(ImageLabel.For(null, obj.Url)));
            else renderer.WriteChildren(obj);
        }
    }

    sealed class PolicyAutolinkRenderer(LinkScope scope) : AvaloniaObjectRenderer<AutolinkInline> {
        protected override void Write(AvaloniaRenderer renderer, AutolinkInline obj) {
            if (scope.Inside || !LinkPolicy.IsOpenable(obj.Url)) {
                renderer.WriteInline(new Run(obj.Url));
                return;
            }
            var link = Hyperlink(obj.Url);
            link.Inlines.Add(new Run(obj.Url));
            renderer.WriteInline(link);
        }
    }

    sealed class SourceHtmlBlockRenderer : AvaloniaObjectRenderer<HtmlBlock> {
        protected override void Write(AvaloniaRenderer renderer, HtmlBlock obj) {
            var text = new MarkdownSelectableTextBlock { TextWrapping = TextWrapping.Wrap };
            text.Classes.Add("markdown-paragraph");
            renderer.Push(text.Inlines!);
            renderer.WriteLeafRawLines(obj);
            renderer.Pop();
            renderer.WriteBlock(text);
        }
    }

    sealed partial class SourceHtmlInlineRenderer : AvaloniaObjectRenderer<HtmlInline> {
        [GeneratedRegex(@"^<br\s*/?\s*>$", RegexOptions.IgnoreCase)]
        private static partial Regex BrTag();

        protected override void Write(AvaloniaRenderer renderer, HtmlInline obj) {
            if (BrTag().IsMatch(obj.Tag.Trim())) renderer.WriteInline(new LineBreak());
            else SourceText.Write(renderer, obj.Tag);
        }
    }
}
```

`src/Capacitor.App/Views/MarkdownView.cs` (replace the whole file; Task 10 extends it):

```csharp
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Capacitor.App.GitHubHtml;
using MarkView.Avalonia;
using MarkView.Avalonia.SyntaxHighlighting;

namespace Capacitor.App.Views;

/// Markdown rendered through MarkView under the app's link policy, image rule and palette.
public sealed class MarkdownView : ContentControl {
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<MarkdownView, string?>(nameof(Text));

    public static readonly StyledProperty<ICommand?> OpenLinkProperty =
        AvaloniaProperty.Register<MarkdownView, ICommand?>(nameof(OpenLink));

    public static readonly StyledProperty<MarkdownFlavor> FlavorProperty =
        AvaloniaProperty.Register<MarkdownView, MarkdownFlavor>(nameof(Flavor));

    // The extension builds its TextMate highlighters on first use and keeps them, so one
    // instance serves the app; a per-view instance rebuilds them on every render.
    static readonly TextMateExtension Highlighting = new();

    readonly MarkdownViewer _viewer = new();

    static MarkdownView() {
        TextProperty.Changed.AddClassHandler<MarkdownView>((view, _) => view._viewer.Markdown = view.Text);
        FlavorProperty.Changed.AddClassHandler<MarkdownView>((view, _) => view.ApplyFlavor());
    }

    public MarkdownView() {
        ApplyFlavor();
        // The viewer's template owns a ScrollViewer; the list around it is what scrolls.
        ScrollViewer.SetVerticalScrollBarVisibility(_viewer, ScrollBarVisibility.Disabled);
        ScrollViewer.SetHorizontalScrollBarVisibility(_viewer, ScrollBarVisibility.Disabled);
        _viewer.LinkClicked += (_, e) => {
            e.Handled = true;
            if (OpenLink is { } open && open.CanExecute(e.Url)) open.Execute(e.Url);
        };
        Content = _viewer;
    }

    public string? Text {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public ICommand? OpenLink {
        get => GetValue(OpenLinkProperty);
        set => SetValue(OpenLinkProperty, value);
    }

    public MarkdownFlavor Flavor {
        get => GetValue(FlavorProperty);
        set => SetValue(FlavorProperty, value);
    }

    void ApplyFlavor() {
        _viewer.Extensions.Clear();
        _viewer.Extensions.Add(Flavor == MarkdownFlavor.GitHub ? KcapMarkdownExtension.GitHub : KcapMarkdownExtension.Chat);
        _viewer.Extensions.Add(Highlighting);
        // Only the pipeline change re-renders, so it goes last.
        _viewer.Pipeline = Flavor == MarkdownFlavor.GitHub ? GitHubPipeline.Instance : null;
    }
}
```

Append to `src/Capacitor.App/Views/MarkdownStyles.axaml`, before the closing `</Styles>`:

```xml
    <Style Selector="mv|MarkdownViewer :is(TextBlock).markdown-pre-text">
        <Setter Property="FontFamily" Value="Menlo,Monaco,Consolas,Cascadia Mono,DejaVu Sans Mono,monospace" />
        <Setter Property="FontSize" Value="12.5" />
        <Setter Property="Foreground" Value="{StaticResource KcapTextBrush}" />
        <Setter Property="Background" Value="Transparent" />
    </Style>
    <Style Selector="mv|MarkdownViewer Border.markdown-alert">
        <Setter Property="Background" Value="Transparent" />
        <Setter Property="BorderBrush" Value="{StaticResource KcapBorderBrush}" />
        <Setter Property="BorderThickness" Value="3,0,0,0" />
        <Setter Property="Padding" Value="12,4" />
        <Setter Property="Margin" Value="0" />
    </Style>
    <Style Selector="mv|MarkdownViewer Border.markdown-alert-note">
        <Setter Property="BorderBrush" Value="{StaticResource KcapInfoBrush}" />
    </Style>
    <Style Selector="mv|MarkdownViewer Border.markdown-alert-tip">
        <Setter Property="BorderBrush" Value="{StaticResource KcapSuccessBrush}" />
    </Style>
    <Style Selector="mv|MarkdownViewer Border.markdown-alert-important">
        <Setter Property="BorderBrush" Value="{StaticResource KcapPurpleBrush}" />
    </Style>
    <Style Selector="mv|MarkdownViewer Border.markdown-alert-warning">
        <Setter Property="BorderBrush" Value="{StaticResource KcapWarningBrush}" />
    </Style>
    <Style Selector="mv|MarkdownViewer Border.markdown-alert-caution">
        <Setter Property="BorderBrush" Value="{StaticResource KcapDangerBrush}" />
    </Style>
    <Style Selector="mv|MarkdownViewer :is(TextBlock).markdown-alert-header">
        <Setter Property="Foreground" Value="{StaticResource KcapMutedBrush}" />
        <Setter Property="FontSize" Value="12.5" />
        <Setter Property="FontWeight" Value="SemiBold" />
    </Style>
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/GitHubMarkdownViewTests/*"`, then `MarkdownViewTests` again.
Expected: PASS (26 and 10 tests). If `The_bot_comment_renders_with_no_literal_tags` fails on the pre's line-break count, print `text.Inlines` — the pre's two source lines must arrive as runs around exactly one `LineBreak`, with the line ends after `<pre>` and before `</pre>` dropped. If the alert's border brush is not the app's, the MarkView theme's own `markdown-alert-note` style is winning: the app's `MarkdownStyles.axaml` is included after the theme in `App.axaml`, so check the selector spelling against the classes `AlertBlockRenderer` adds (`markdown-alert`, `markdown-alert-note`).

Build: `dotnet build src/Capacitor.App/Capacitor.App.csproj`
Expected: 0 warnings.

- [ ] **Step 6: Commit**

```bash
git add src/Capacitor.App/Views/MarkdownFlavor.cs src/Capacitor.App/Views/SourceText.cs src/Capacitor.App/Views/LinkScope.cs src/Capacitor.App/Views/HtmlPreBlockRenderer.cs src/Capacitor.App/Views/KcapMarkdownExtension.cs src/Capacitor.App/Views/MarkdownView.cs src/Capacitor.App/Views/MarkdownStyles.axaml test/Capacitor.App.Tests.Unit/MarkdownViewHarness.cs test/Capacitor.App.Tests.Unit/GitHubMarkdownViewTests.cs test/Capacitor.App.Tests.Unit/MarkdownViewTests.cs
git commit -m "Give MarkdownView a GitHub flavor with image links and pre blocks (#982)" -m "An image is a link labelled with its alt text and is never fetched; a hyperlink is never written inside another, in either flavor. Source text goes out as runs split at line ends: a line end inside a run hangs layout." -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 10: Rendering `<details>`

A collapsed details section renders its header and nothing else. MarkView's selection index is built once per render and ignores visibility, so hidden content would be copied and would take clicks meant for what follows it. A toggle records the new state in the view and re-renders.

**Files:**
- Create: `src/Capacitor.App/Views/DetailsState.cs`, `DetailsExtension.cs`, `DetailsBlockRenderer.cs`
- Modify: `src/Capacitor.App/Views/MarkdownView.cs`, `MarkdownStyles.axaml`
- Test: `test/Capacitor.App.Tests.Unit/DetailsViewTests.cs`

**Interfaces:**
- Consumes: `DetailsBlock { Summary, IsOpen, Ordinal }`, `KcapMarkdownExtension.GitHub`.
- Produces: `DetailsState { IsExpanded(int ordinal, bool isOpen) : bool; Set(int, bool); Clear() }`; `DetailsExtension(DetailsState, Action<int, bool> toggle) : IMarkViewExtension`; `DetailsBlockRenderer(DetailsState, Action<int, bool>)`. The header is a `ToggleButton.markdown-details-summary` whose `Tag` is the ordinal and whose `IsChecked` is the expanded state; its label is a `TextBlock.markdown-details-label`; the frame is `Border.markdown-details`.

- [ ] **Step 1: Write the failing tests**

`test/Capacitor.App.Tests.Unit/DetailsViewTests.cs`:

```csharp
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Capacitor.App.Views;
using static Capacitor.App.Tests.Unit.AvaloniaSession;
using static Capacitor.App.Tests.Unit.MarkdownViewHarness;

namespace Capacitor.App.Tests.Unit;

public class DetailsViewTests {
    const string OneSection = "<details>\n<summary><strong>Agent Prompt</strong></summary>\n\nhidden body\n\n</details>\n\nafter [v](https://visible.example)";

    static IEnumerable<ToggleButton> Headers(Visual root) => All<ToggleButton>(root).Where(b => b.Classes.Contains("markdown-details-summary"));

    static ToggleButton Header(Visual root, int ordinal) => Headers(root).Single(b => b.Tag is int tag && tag == ordinal);

    static string HeaderText(ToggleButton header) => Reads((TextBlock)header.Content!);

    /// A pointer click on the header's centre; a toggle re-renders, so callers re-query the tree.
    static void ClickHeader(Window window, ToggleButton header) {
        var point = header.TranslatePoint(new Point(header.Bounds.Width / 2, header.Bounds.Height / 2), window)!.Value;
        window.MouseMove(point);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    static string SelectedText(Visual root) {
        var viewer = Viewer(root);
        viewer.SelectAll();
        return viewer.GetSelectedText();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_details_section_starts_collapsed_with_its_summary_as_the_header() {
        await RunOnUiAsync(async () => {
            var (window, root, _) = Show(OneSection, MarkdownFlavor.GitHub);
            try {
                var header = Header(root, 0);
                await Assert.That(header.IsChecked).IsFalse();
                await Assert.That(HeaderText(header)).Contains("Agent Prompt");
                await Assert.That(Spans<Avalonia.Controls.Documents.Bold>(((TextBlock)header.Content!).Inlines!).Count()).IsEqualTo(1);
                await Assert.That(All<TextBlock>(root).Select(Reads).Any(t => t.Contains("hidden body"))).IsFalse();
                await Assert.That(All<Border>(root).Count(b => b.Classes.Contains("markdown-details"))).IsEqualTo(1);
            } finally { window.Close(); }
        });
    }

    /// Pins the selection semantics: collapsed content is not rendered, so select-all skips it;
    /// expanded, it is there; collapsed again, it is gone again.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Collapsed_content_is_absent_from_a_selection_and_returns_when_expanded() {
        await RunOnUiAsync(async () => {
            var (window, root, _) = Show(OneSection, MarkdownFlavor.GitHub);
            try {
                await Assert.That(SelectedText(root)).DoesNotContain("hidden body");
                ClickHeader(window, Header(root, 0));
                await Assert.That(Header(root, 0).IsChecked).IsTrue();
                await Assert.That(SelectedText(root)).Contains("hidden body");
                ClickHeader(window, Header(root, 0));
                await Assert.That(Header(root, 0).IsChecked).IsFalse();
                await Assert.That(SelectedText(root)).DoesNotContain("hidden body");
            } finally { window.Close(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Nested_sections_render_only_inside_an_expanded_parent() {
        await RunOnUiAsync(async () => {
            const string nested = "<details>\n<summary>Outer</summary>\n\n<details>\n<summary>Inner</summary>\n\ninner body\n\n</details>\n\n</details>";
            var (window, root, _) = Show(nested, MarkdownFlavor.GitHub);
            try {
                await Assert.That(Headers(root).Count()).IsEqualTo(1);
                ClickHeader(window, Header(root, 0));
                await Assert.That(Headers(root).Count()).IsEqualTo(2);
                await Assert.That(SelectedText(root)).DoesNotContain("inner body");
                ClickHeader(window, Header(root, 1));
                await Assert.That(SelectedText(root)).Contains("inner body");
                ClickHeader(window, Header(root, 1));
                await Assert.That(SelectedText(root)).DoesNotContain("inner body");
                await Assert.That(Header(root, 0).IsChecked).IsTrue();
            } finally { window.Close(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_open_section_starts_expanded_and_a_missing_summary_reads_Details() {
        await RunOnUiAsync(async () => {
            var (window, root, _) = Show("<details open>\n\nshown body\n\n</details>", MarkdownFlavor.GitHub);
            try {
                var header = Header(root, 0);
                await Assert.That(header.IsChecked).IsTrue();
                await Assert.That(HeaderText(header)).Contains("Details");
                await Assert.That(SelectedText(root)).Contains("shown body");
            } finally { window.Close(); }
        });
    }

    /// A stale index entry for hidden content would take this click; the re-render keeps the index
    /// true to what is visible, whether the section was never expanded or expanded and collapsed.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_link_after_a_collapsed_section_opens_its_own_url() {
        await RunOnUiAsync(async () => {
            var (window, root, opened) = Show(OneSection, MarkdownFlavor.GitHub);
            try {
                var (block, link) = AllLinks(root).Single();
                Click(window, block, link);
                await Assert.That(opened).IsEquivalentTo(new[] { "https://visible.example" });

                ClickHeader(window, Header(root, 0));
                ClickHeader(window, Header(root, 0));
                (block, link) = AllLinks(root).Single();
                Click(window, block, link);
                await Assert.That(opened).IsEquivalentTo(new[] { "https://visible.example", "https://visible.example" });
            } finally { window.Close(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_header_toggles_on_space_and_keeps_focus_across_the_re_render() {
        await RunOnUiAsync(async () => {
            var (window, root, _) = Show(OneSection, MarkdownFlavor.GitHub);
            try {
                Header(root, 0).Focus();
                window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
                window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                await Assert.That(Header(root, 0).IsChecked).IsTrue();
                await Assert.That(SelectedText(root)).Contains("hidden body");
                await Assert.That(TopLevel.GetTopLevel(root)!.FocusManager!.GetFocusedElement()).IsEqualTo(Header(root, 0));
            } finally { window.Close(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Changing_the_text_resets_the_expanded_state() {
        await RunOnUiAsync(async () => {
            var (window, view, _) = Show(OneSection, MarkdownFlavor.GitHub);
            try {
                ClickHeader(window, Header(view, 0));
                await Assert.That(Header(view, 0).IsChecked).IsTrue();
                view.Text = "<details>\n<summary>Other</summary>\n\nother body\n\n</details>";
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                await Assert.That(Header(view, 0).IsChecked).IsFalse();
                await Assert.That(HeaderText(Header(view, 0))).Contains("Other");
            } finally { window.Close(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_press_on_a_summary_link_toggles_and_opens_nothing() {
        await RunOnUiAsync(async () => {
            var (window, root, opened) = Show("<details>\n<summary><a href=\"https://s.example\">S</a></summary>\n\nbody\n\n</details>", MarkdownFlavor.GitHub);
            try {
                await Assert.That(AllLinks(root)).IsEmpty();
                ClickHeader(window, Header(root, 0));
                await Assert.That(Header(root, 0).IsChecked).IsTrue();
                await Assert.That(opened).IsEmpty();
            } finally { window.Close(); }
        });
    }

    /// Pins the pre's shape against the list-item walker once more, this time inside an expanded
    /// details inside the item.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_pre_inside_an_expanded_section_in_a_list_item_is_selectable() {
        await RunOnUiAsync(async () => {
            var (window, root, _) = Show("- <details open>\n  <summary>S</summary>\n\n  <pre>\n  alpha\n  beta\n  </pre>\n\n  </details>", MarkdownFlavor.GitHub);
            try {
                var selected = SelectedText(root);
                await Assert.That(selected).Contains("alpha");
                await Assert.That(selected).Contains("beta");
            } finally { window.Close(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    [Timeout(60_000)]
    public async Task Adversarial_nesting_renders_without_throwing(CancellationToken _) {
        await RunOnUiAsync(async () => {
            var details = string.Concat(Enumerable.Repeat("<details open>\n\n", 50)) + "x\n\n" + string.Concat(Enumerable.Repeat("</details>\n\n", 50));
            var (window, root, _) = Show(details, MarkdownFlavor.GitHub);
            try {
                await Assert.That(Headers(root).Count()).IsEqualTo(8);
                await Assert.That(All<TextBlock>(root).Select(Reads).Any(t => t.Contains('x'))).IsTrue();
            } finally { window.Close(); }

            var bold = "p " + string.Concat(Enumerable.Repeat("<b>", 200)) + "x" + string.Concat(Enumerable.Repeat("</b>", 200));
            var (boldWindow, boldRoot, _) = Show(bold, MarkdownFlavor.GitHub);
            try {
                await Assert.That(Paragraphs(boldRoot).Single().Bounds.Height).IsGreaterThan(0);
            } finally { boldWindow.Close(); }
        });
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/DetailsViewTests/*"`
Expected: FAIL — no `ToggleButton` is rendered; `Header(root, 0)` throws.

- [ ] **Step 3: Write the details types**

`src/Capacitor.App/Views/DetailsState.cs`:

```csharp
namespace Capacitor.App.Views;

/// Which details sections of one view the user has toggled, by ordinal. A section nobody touched
/// follows its `open` attribute.
public sealed class DetailsState {
    readonly Dictionary<int, bool> _expanded = new();

    public bool IsExpanded(int ordinal, bool isOpen) => _expanded.TryGetValue(ordinal, out var expanded) ? expanded : isOpen;

    public void Set(int ordinal, bool expanded) => _expanded[ordinal] = expanded;

    public void Clear() => _expanded.Clear();
}
```

`src/Capacitor.App/Views/DetailsExtension.cs`:

```csharp
using MarkView.Avalonia.Extensions;
using MarkView.Avalonia.Rendering;

namespace Capacitor.App.Views;

/// Per view, because the details state is the view's.
public sealed class DetailsExtension(DetailsState state, Action<int, bool> toggle) : IMarkViewExtension {
    public void Register(AvaloniaRenderer renderer) => renderer.ObjectRenderers.Add(new DetailsBlockRenderer(state, toggle));
}
```

`src/Capacitor.App/Views/DetailsBlockRenderer.cs`:

```csharp
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Capacitor.App.GitHubHtml;
using MarkView.Avalonia.Rendering;

namespace Capacitor.App.Views;

/// A collapsed section renders its header and nothing else: the selection index is built once
/// per render and ignores visibility, so hidden content would be copied and would take clicks.
sealed class DetailsBlockRenderer(DetailsState state, Action<int, bool> toggle) : AvaloniaObjectRenderer<DetailsBlock> {
    protected override void Write(AvaloniaRenderer renderer, DetailsBlock obj) {
        var expanded = state.IsExpanded(obj.Ordinal, obj.IsOpen);

        var label = new TextBlock { TextWrapping = TextWrapping.Wrap };
        label.Classes.Add("markdown-details-label");
        renderer.Push(label.Inlines!);
        renderer.WriteInline(new Run(expanded ? "▾ " : "▸ "));
        if (obj.Summary.FirstChild is null) renderer.WriteInline(new Run("Details"));
        else renderer.WriteChildren(obj.Summary);
        renderer.Pop();

        var header = new ToggleButton { Content = label, IsChecked = expanded, Tag = obj.Ordinal };
        header.Classes.Add("markdown-details-summary");
        header.Click += (_, _) => toggle(obj.Ordinal, header.IsChecked == true);

        var panel = new StackPanel { Spacing = 8, Children = { header } };
        if (expanded) {
            var content = new StackPanel { Spacing = 8 };
            renderer.Push(content);
            renderer.WriteChildren(obj);
            renderer.Pop();
            panel.Children.Add(content);
        }

        var border = new Border { Child = panel };
        border.Classes.Add("markdown-details");
        renderer.WriteBlock(border);
    }
}
```

The summary sits inside a button, which neither selection walker descends, so its text is not selectable; in return the header takes focus and toggles on Space and Enter.

- [ ] **Step 4: Wire the state into `MarkdownView`**

In `src/Capacitor.App/Views/MarkdownView.cs`:

Add the usings `Avalonia.Threading` and `Avalonia.VisualTree`. Add the fields after `_viewer`:

```csharp
    readonly DetailsState _details = new();
    readonly DetailsExtension _detailsExtension;
```

Change the static constructor's `Text` handler and the constructor's first lines:

```csharp
    static MarkdownView() {
        TextProperty.Changed.AddClassHandler<MarkdownView>((view, _) => {
            view._details.Clear();
            view._viewer.Markdown = view.Text;
        });
        FlavorProperty.Changed.AddClassHandler<MarkdownView>((view, _) => view.ApplyFlavor());
    }

    public MarkdownView() {
        _detailsExtension = new(_details, OnDetailsToggled);
        ApplyFlavor();
```

Replace `ApplyFlavor` and add the toggle handler:

```csharp
    void ApplyFlavor() {
        _details.Clear();
        _viewer.Extensions.Clear();
        if (Flavor == MarkdownFlavor.GitHub) {
            _viewer.Extensions.Add(KcapMarkdownExtension.GitHub);
            _viewer.Extensions.Add(_detailsExtension);
        } else {
            _viewer.Extensions.Add(KcapMarkdownExtension.Chat);
        }
        _viewer.Extensions.Add(Highlighting);
        // Only the pipeline change re-renders, so it goes last.
        _viewer.Pipeline = Flavor == MarkdownFlavor.GitHub ? GitHubPipeline.Instance : null;
    }

    /// Re-rendering rebuilds MarkView's selection index, so it always matches what is visible.
    /// The header that was pressed is gone with the old tree; its successor gets the focus back.
    void OnDetailsToggled(int ordinal, bool expanded) {
        _details.Set(ordinal, expanded);
        var hadFocus = Header(ordinal)?.IsFocused == true;
        Dispatcher.UIThread.Post(() => {
            var text = Text;
            _viewer.Markdown = null;
            _viewer.Markdown = text;
            if (hadFocus) Header(ordinal)?.Focus();
        });
    }

    ToggleButton? Header(int ordinal) =>
        this.GetVisualDescendants().OfType<ToggleButton>().FirstOrDefault(button => button.Tag is int tag && tag == ordinal);
```

The re-render is posted rather than run inside the button's own click handling, so the tree is not replaced while the event that toggled it is still being routed; both `Markdown` assignments run in that one posted callback, with no frame between them.

Append to `src/Capacitor.App/Views/MarkdownStyles.axaml`, before the closing `</Styles>`:

```xml
    <Style Selector="mv|MarkdownViewer Border.markdown-details">
        <Setter Property="BorderBrush" Value="{StaticResource KcapBorderBrush}" />
        <Setter Property="BorderThickness" Value="1" />
        <Setter Property="CornerRadius" Value="8" />
        <Setter Property="Padding" Value="10,6" />
        <Setter Property="Margin" Value="0" />
    </Style>
    <Style Selector="mv|MarkdownViewer ToggleButton.markdown-details-summary">
        <Setter Property="Background" Value="Transparent" />
        <Setter Property="BorderThickness" Value="0" />
        <Setter Property="Padding" Value="0" />
        <Setter Property="MinHeight" Value="0" />
        <Setter Property="HorizontalAlignment" Value="Stretch" />
        <Setter Property="HorizontalContentAlignment" Value="Left" />
        <Setter Property="Cursor" Value="Hand" />
    </Style>
    <Style Selector="mv|MarkdownViewer ToggleButton.markdown-details-summary /template/ ContentPresenter#PART_ContentPresenter">
        <Setter Property="Background" Value="Transparent" />
        <Setter Property="BorderThickness" Value="0" />
    </Style>
    <Style Selector="mv|MarkdownViewer ToggleButton.markdown-details-summary:checked /template/ ContentPresenter#PART_ContentPresenter">
        <Setter Property="Background" Value="Transparent" />
    </Style>
    <Style Selector="mv|MarkdownViewer ToggleButton.markdown-details-summary:pointerover /template/ ContentPresenter#PART_ContentPresenter">
        <Setter Property="Background" Value="{StaticResource KcapSurfaceRaisedBrush}" />
    </Style>
    <Style Selector="mv|MarkdownViewer ToggleButton.markdown-details-summary:pressed /template/ ContentPresenter#PART_ContentPresenter">
        <Setter Property="Background" Value="{StaticResource KcapSurfaceRaisedBrush}" />
    </Style>
    <Style Selector="mv|MarkdownViewer :is(TextBlock).markdown-details-label">
        <Setter Property="Foreground" Value="{StaticResource KcapTextBrush}" />
        <Setter Property="FontSize" Value="13.5" />
        <Setter Property="FontWeight" Value="SemiBold" />
    </Style>
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/DetailsViewTests/*"`, then `GitHubMarkdownViewTests` and `MarkdownViewTests`.
Expected: PASS (10, 26 and 10 tests). If the focus test fails, the posted re-render ran before the test's `RunJobs`: the assertion reads the focused element after `RunJobs`, so check that `Header(ordinal)?.Focus()` ran after the new tree was attached — moving it to `Dispatcher.UIThread.Post(..., DispatcherPriority.Loaded)` inside the callback is the fix.

Build: `dotnet build src/Capacitor.App/Capacitor.App.csproj`
Expected: 0 warnings.

- [ ] **Step 6: Commit**

```bash
git add src/Capacitor.App/Views/DetailsState.cs src/Capacitor.App/Views/DetailsExtension.cs src/Capacitor.App/Views/DetailsBlockRenderer.cs src/Capacitor.App/Views/MarkdownView.cs src/Capacitor.App/Views/MarkdownStyles.axaml test/Capacitor.App.Tests.Unit/DetailsViewTests.cs
git commit -m "Render details sections collapsed and re-render on toggle (#982)" -m "MarkView's selection index is built once per render and ignores visibility, so a collapsed section renders nothing but its header and a toggle re-renders the view." -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 11: The reader opts in, change notes, follow-ups

**Files:**
- Modify: `src/Capacitor.App/Views/PullRequestReader.axaml`, `docs/CHANGES.md`

- [ ] **Step 1: Switch the reader's two views**

In `src/Capacitor.App/Views/PullRequestReader.axaml`, both `<views:MarkdownView …>` elements — the description (`Text="{Binding Description}"`) and the comment body (`Text="{Binding Body}"`) — gain the attribute `Flavor="GitHub"`.

- [ ] **Step 2: Write the change note**

At the top of `docs/CHANGES.md`, after the intro paragraphs and before the first `##` entry, add:

```markdown
## The pull request reader renders GitHub-flavoured markdown

Review bots write their findings almost entirely in HTML, and the reader showed the markup as
text. A Markdig `DocumentProcessed` pass (`Capacitor.App.GitHubHtml`) rewrites the HTML the reader
understands into standard Markdig nodes — formatting tags to emphasis, `<a>` to links, `<img>` to
image links labelled with their alt text, `<br>` to hard breaks — so MarkView's renderers and the
app's link policy apply unchanged; only `<details>` and `<pre>` have node types of their own. Chat
is untouched: `MarkdownView.Flavor` selects the pipeline, and only the reader opts in. Unmatched
or rejected HTML renders as its source, all or nothing per HTML block; comments inside converted
content vanish, as on github.com.

Three library facts shaped the design. MarkView builds its selection index once per render and
never checks visibility, so a collapsed details section renders its header and nothing else, and
a toggle re-renders the view. MarkView dispatches a click only for a hyperlink that is a direct
inline of its text block, so the pass hoists links above their emphasis instead of owning
hit-testing — every Avalonia route from a point to text geometry is quadratic in a line's runs.
Markdig's renderer throws past 128 nested containers after parsing has returned, so synthesized
nesting is budgeted at 100 before anything mutates, and normalisation runs before anything
measures a height.
```

- [ ] **Step 3: Verify the whole change**

Build: `dotnet build src/Capacitor.App/Capacitor.App.csproj`
Expected: 0 warnings, 0 errors.

Run each of the test classes once more:
`dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/HtmlTokenizerTests/*"` and likewise for `HtmlTagsTests`, `ImageLabelTests`, `NormalisationTests`, `InlinePairingTests`, `LinkHoistingTests`, `HtmlBlockRuleTests`, `DetailsFoldTests`, `PassTotalityTests`, `MarkdownViewTests`, `GitHubMarkdownViewTests`, `DetailsViewTests`, and the reader's own `PullRequestPresentationTests`.
Expected: all PASS.

- [ ] **Step 4: Commit**

```bash
git add src/Capacitor.App/Views/PullRequestReader.axaml docs/CHANGES.md
git commit -m "Render pull request bodies in the GitHub flavor (#982)" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

- [ ] **Step 5: Record the follow-ups on the issue**

Posting to the public repository is outward-facing: show the owner this comment and post it only when they agree.

```
gh issue comment 982 -R kurrent-io/kcap-cli --body "Follow-ups deferred from the first PR, each its own issue when picked up:
- HTML shown as source for now: <table>, <p>, <div>, <span>, headings and lists written as HTML, a <summary> in a separate block from its <details>, <picture>.
- @mentions, #123 and commit references; suggestion fences; loading images.
- Links inside formatting stay inert in the chat flavor (**[x](url)**): chat runs no pass to hoist them.
- Upstream, MarkView.Avalonia: a press resolves to the nearest caret stop, so the trailing half of a link's last glyph misses it; its hover lookup goes through TextLine.GetCharacterHitFromDistance, which is quadratic in a line's runs.
- Upstream, Avalonia: TextLineImpl.GetRunTextSourcePosition scans the run index from zero for every run GetCharacterHitFromDistance passes."
```

---

## Self-review notes

Spec coverage, decision by decision: D1 Tasks 3–7; D2 Tasks 9–11; D3 tokenizer Task 1, text and tag classes Tasks 2 and 6, inline rule Task 4, block rule Task 6, depth budget Tasks 3–7; D4 Tasks 7 and 10; D5 Tasks 6 and 9; D6 Tasks 3 and 9; D7 Task 6; D8 Tasks 3 and 5 with the accepted limits recorded in `CHANGES.md` in Task 11; D9 Tasks 9 and 10. Error handling: the boundary catch in Task 3, totality in Task 8. Out of scope: Task 11's comment.

Two deviations from the spec, both recorded in it: the pass's namespace is `Capacitor.App.GitHubHtml` (Task 1's constraint), and a line end directly before `</pre>` is dropped as well as the one after `<pre>` (Task 6, Step 8).
