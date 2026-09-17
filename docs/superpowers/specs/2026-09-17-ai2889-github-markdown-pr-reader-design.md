# AI-2889 — GitHub-flavoured markdown in the pull request reader (design)

GitHub: [kcap-cli#982](https://github.com/kurrent-io/kcap-cli/issues/982). Linear: [AI-2889](https://linear.app/kurrent/issue/AI-2889).

## Problem

The desktop pull request reader renders the description and every review and thread comment through `MarkdownView`, the view the chat uses. That view shows HTML as its source text, so a comment written for github.com shows its markup instead of its content. Review bots write their findings almost entirely in HTML, which makes those the hardest comments to read in the app:

```markdown
<img src="https://img.shields.io/badge/Medium-634FD1?style=flat-square" height="20px" alt="Remediation recommended">

1. The title breaks casing <code>📘 Rule violation</code> <code>§ Compliance</code>

<pre>
The supplied title <b><i>Name the repo&#x27;s projects</i></b> begins its
imperative clause with a capital.
</pre>

<details>
<summary><strong>Agent Prompt</strong></summary>

...

</details>
```

Every tag above renders as text, and the `&#x27;` stays undecoded.

## Current state (what the change builds on)

- `MarkdownView` (`src/Capacitor.App/Views/`) wraps MarkView.Avalonia's `MarkdownViewer`, adds two shared extensions (`KcapMarkdownExtension`, `TextMateExtension`) and forwards `LinkClicked` to the bound `OpenLink` command. It sets no pipeline, so MarkView's default applies: `UseSupportedExtensions()` — autolinks, emphasis extras (strikethrough, sub/superscript, inserted, marked), pipe and grid tables, task lists, emoji shortcodes, YAML front matter. Alert blocks are not in it.
- `KcapMarkdownExtension` replaces four MarkView renderers: links and autolinks exist only where `LinkPolicy.IsOpenable` holds (absolute http/https), an image is written as its source text `![label](url)`, an HTML block is written from its raw lines, an HTML inline is written as its tag (`<br>` alone becomes a line break). Its renderers are created per `Register` call, which is per render. It also clears `renderer.ImageLoaders`, so nothing is ever fetched.
- MarkView's extension surface is public: `MarkdownViewer.Pipeline` takes any Markdig pipeline; `IMarkViewExtension.Register(AvaloniaRenderer)` runs on a fresh renderer for every render; `AvaloniaRenderer` exposes `ReplaceOrAdd<T>`, `Push(Panel)`, `Push(InlineCollection)`, `Pop`, `WriteBlock`, `WriteInline`. `ReplaceOrAdd<T>` removes only a renderer of type `T`, so a second extension cannot displace a renderer the first one substituted. The viewer re-renders when `Markdown` or `Pipeline` changes and has no public re-render call.
- MarkView already renders the Markdig nodes this design produces: `EmphasisInline` by delimiter (`*`×2 bold, `*`×1 italic, `~`×2 strikethrough, `~`×1 subscript, `^`×1 superscript, `+`×2 underline), `CodeInline` (as one `Run` holding `Content`), `LinkInline`, hard `LineBreakInline` (as a `LineBreak`), `AlertBlock`.
- Markdig does not parse HTML into a tree. In a paragraph, `<b><i>x</i></b>` is four `HtmlInline` tag nodes around a literal — and so are `<pre>` or `<details>` written mid-paragraph. At block level, `<details>` opens an `HtmlBlock` that ends at the first blank line; the markdown after it parses as ordinary sibling blocks; `</details>` is another `HtmlBlock`. A `<pre>` block runs to the line holding `</pre>` and may contain blank lines. An `<img>` alone on a line is an `HtmlBlock`. A comment-type `HtmlBlock` keeps the whole of its closing line, so `<!-- m -->Visible` is one such block, and an unterminated `<!--` gets that type too. An `HtmlBlock` carries raw lines only — no inlines, no entity decoding. `DocumentProcessed` fires after inline parsing.
- Markdig's renderer counts container nesting in `WriteChildren` and throws past 128. MarkView renders after `Markdown.Parse` has returned.
- MarkView's document-wide selection is an index built once per render by two walkers over the rendered tree. Both index a `MarkdownSelectableTextBlock` and descend `Panel`s and a `Border` whose child is a `Panel`. Only the top-level walker also indexes a `Border.markdown-code-block` whose child is a `TextBlock`; the list-item walker has no such case, so a fenced code block inside a list item is not selectable today. Neither descends any other control. `SelectAll`, `GetSelectedText` and its hit-testing use every indexed entry and never check visibility, and nothing re-indexes after the render.
- MarkView fires `LinkClicked` only for a `MarkdownHyperlink` that is a direct inline of an indexed `MarkdownSelectableTextBlock`. A link nested in a span (`**[x](url)**`) or sitting in any other text block is drawn as a link and does nothing; this holds in chat today. It resolves the press through `TextLayout.HitTestPoint`'s caret position and then scans the block's inlines from the first, and it repeats that lookup on every pointer move to set the hand cursor. An unhandled `LinkClicked` does not navigate.
- `MarkdownViewTests` pins the chat behaviour: images keep their source text, HTML shows as source, multi-line HTML lays out under a height-unconstrained parent, an allowed link opens through the command on a simulated click.
- Pull request bodies reach the reader through two readers (the local `gh` reader and the server reader), both carrying the markdown source.

## Decisions

### D1 — HTML is normalised into the Markdig tree, not rendered by new renderers

A pass over the parsed document rewrites the HTML it understands into standard Markdig nodes. The existing renderers — including the policy link renderer — then apply unchanged, so `<a href="javascript:…">` is inert for the same reason a markdown link with that target is. Only `<details>` and `<pre>` have no markdown equivalent and get their own node types and renderers.

Rejected: stateful renderers that push a container on an open tag and pop on a close tag — a renderer cannot look ahead, so an unclosed tag leaves the container stack unbalanced and corrupts everything after it, and "degrade to source" cannot be decided at the open tag. Rejected: rewriting the source text to markdown before parsing — patterns over raw text must re-implement code-fence and escape rules to avoid rewriting inside them. Rejected: asking GitHub for the rendered body (`bodyHTML`) — it needs both readers and the server contract to change, and still needs an HTML-to-controls mapper.

### D2 — A flavor on `MarkdownView`; chat rendering is unchanged

`MarkdownView` gets a `Flavor` styled property: `MarkdownFlavor.Chat` (default) and `MarkdownFlavor.GitHub`. `PullRequestReader.axaml` sets `Flavor="GitHub"` on its two views (description, comment body). Chat, system notes and user turns keep today's rendering: a transcript is not written for github.com, and a tag there is more likely content than markup.

- `Chat`: `Pipeline` stays unset; the shared chat-mode `KcapMarkdownExtension` instance is registered.
- `GitHub`: `Pipeline` is one shared static pipeline — `UseSupportedExtensions()`, `UseAlertBlocks()`, and the pass of D3; the shared GitHub-mode `KcapMarkdownExtension` instance is registered instead of the chat one, plus one `DetailsExtension` instance owned by the view (D4), because details state is per view.

`KcapMarkdownExtension` is one type with a mode, because of the `ReplaceOrAdd` rule above: the image rule (D6) lives in the same link renderer that enforces `LinkPolicy`. Applying a flavor clears the view's details state, swaps the extensions in `_viewer.Extensions` and then sets `Pipeline`, since only the pipeline change re-renders.

### D3 — The pass

`GitHubHtmlExtension : IMarkdownExtension` subscribes to the pipeline builder's `DocumentProcessed`. It lives under `src/Capacitor.App/GitHubHtml/` (namespace `Capacitor.App.GitHubHtml`) with its tokenizer and node types, one type per file — not `Capacitor.App.Markdown`: a namespace of that name hides Markdig's `Markdown` class from every file under `Capacitor.App` — and depends on Markdig only — no Avalonia type — so it is testable without a UI session.

**Tokenizer.** `HtmlTokenizer` turns a string into tokens: tag (name, attributes, open/close/self-closing), text, comment. It is a single forward scan: no regular expressions, no recursion, linear in the input, and it never throws. Attribute values may be double-quoted, single-quoted or bare; names compare ordinal-ignore-case. A comment token is `<!--` through the first `-->`. Input it cannot finish — `<!--` with no `-->`, or `<` followed by a letter or `/` with no closing `>` — is returned as text and sets the result's `Malformed` flag. A `<` followed by anything else is plain text and not malformed.

**Text.** Text and attribute values are entity-decoded with `WebUtility.HtmlDecode` first; then, outside `<pre>`, every run of whitespace (decoded `&#10;` included) collapses to one space. Inside `<pre>` whitespace is kept and line ends are handled by D5. Nothing this design writes ever puts `\r` or `\n` inside a `Run`: Avalonia's line breaker does not finish laying out a newline inside a `Run` under a height-unconstrained parent. That covers every synthesized `LiteralInline` and `CodeInline`, every image label (D6), which is normalised after it is chosen, and the source fallback itself: Markdig keeps a tag that spans lines (`<span\n title="x">`) as one `HtmlInline` with the line end inside `Tag`, and `SourceHtmlInlineRenderer` writes that as a single `Run` today, in chat too. Source text is therefore written through one helper, in both flavors, that splits at `\r\n`, `\r` and `\n` and emits the pieces as `Run`s separated by `LineBreak` inlines, so the text is preserved and no `Run` holds a line end. HTML blocks already go out line by line.

**Tags.**

| Inline tag | Becomes |
|---|---|
| `b`, `strong` | `EmphasisInline` `*`×2 |
| `i`, `em` | `EmphasisInline` `*`×1 |
| `del`, `s`, `strike` | `EmphasisInline` `~`×2 |
| `ins` | `EmphasisInline` `+`×2 |
| `sub` / `sup` | `EmphasisInline` `~`×1 / `^`×1 |
| `code`, `kbd`, `tt`, `samp` | `CodeInline` holding the plain text of its content, a line break inside it counting as one space |
| `a` | `LinkInline` with `Url` = `href`; no `href` → its children, unwrapped |
| `img` | image `LinkInline` with `Url` = `src` and one literal child, its label from `alt` and `src` (D6) |
| `br` | hard `LineBreakInline` |

`details`, `summary` and `pre` are **block-only** tags (D4, D5). Only `href`, `src`, `alt` and `open` are read; every other attribute is ignored. A tag in neither list is unknown.

Tags fall into three classes, and both rules below dispatch on them:

- **Void** — `img`, `br`. They convert on their own, with or without a trailing slash, and never open anything. A close tag for one (`</br>`, `</img>`) is an error: source in an inline container, a rejection in a block.
- **Structural** — `details`. Its tags never enter a balancing stack; they belong to D4 alone.
- **Paired** — every other inline tag, `summary` and `pre`. A trailing slash on one (`<b/>`) is ignored, as HTML does, so it is an open tag.

`CodeInline` is a leaf, so formatting nested inside `<code>` is flattened to its text. That loses `<code><b>x</b></code>`'s bold and nothing else.

**Inline rule** — lenient, per pair. For each inline container, children first, then the container itself, walk its children once with a stack of open inline tags. A close tag matches the nearest open tag of the same name. Open tags above the match are discarded for good: they stay `HtmlInline` and can never match a later close tag, so `<b><i>x</b>y</i>` becomes bold around `<i>x` followed by `y</i>`, and no synthesized node ever crosses another. A matched pair and the inlines between become the mapped node. `img` and `br` convert on their own. Everything unmatched — an open or close tag without a partner, a pair split across two containers (`**<b>x**</b>`), any block-only or unknown tag in an inline container — stays an `HtmlInline` and renders as source. A complete inline comment is removed. The walk is amortised constant per inline: the nearest open tag of a name is found through a per-name index, not by searching the stack.

**Block rule** — strict, all-or-nothing per `HtmlBlock`, comment-type blocks included. The block is tokenized once and checked with a stack that holds its open **paired** tags and nothing else. It is **rejected**, left untouched and rendered as source (its comments too), when any of these holds:

- the result is malformed, or it holds an unknown tag or a close tag for a void tag;
- a paired close tag does not match the innermost open paired tag, or finds the stack empty;
- a paired tag is still open at the end of the block (`<b>` on a line of its own, then `x`);
- a `details` open or close tag appears while the stack is not empty. `details` tags are never pushed or popped: they require an empty stack and are handed to D4, so a block that is just `</details>` passes this rule, and no paired element can straddle a details boundary (`<details><b>x</details>y</b>` rejects);
- a `summary` or `pre` is misplaced (D4, D5);
- a node it would synthesize would exceed the depth budget;
- D4 rejects it.

These are evaluated for every block of a container first; D4's matching and propagation run next; mutation is last. Otherwise the block **converts**: comment tokens vanish, inline content becomes `ParagraphBlock`s of synthesized inlines, `pre` becomes an `HtmlPreBlock`, `details` tags fold under D4. A block left with nothing — only comments and whitespace — is removed from the tree.

**Depth budget.** A node's depth is the number of containers above it, which is what Markdig's renderer counts before throwing past 128. `MaxDepth` is 100. A conversion is refused when it would put any node deeper than that: for a wrap — an inline pair, a details fold — when depth(new node) + 1 + height(what it wraps) exceeds `MaxDepth`. The same holds for a conversion that wraps nothing but brings a child with it: an `img` tag becomes a link around its label, and D8's normalisations give an autolink and a childless image a literal child. Each is refused when that literal would sit deeper than `MaxDepth`. A refused inline pair or `img` stays source tags; a refused normalisation leaves the node as parsed (D8 says how each still renders); a refused block is rejected; a refused details pair is a rejected pair (D4).

Order makes the heights true. For each leaf block's inline tree: D8's normalisations first, so that every height the inline rule measures already includes the children they add; then the inline rule; then D8's hoisting, which never deepens anything. A container's details fold is budgeted only after every block in it has been through all three. Heights come from the same post-order walk — each stack entry carries the greatest height seen since its open tag and hands it to the entry below when it goes — so the check is constant per pair. The decision is made before anything mutates.

**Work.** The pass is linear in the document: each `HtmlBlock` is tokenized once, each inline is visited once, D4's propagation visits each tag once, and D8's hoisting creates at most 16 nodes per link. Its own recursion follows tree depth, which the parser's limit and `MaxDepth` bound; nothing recurses per token.

**Degrade rule.** Unmatched or rejected means source — never nothing, never a guess. HTML comments inside converted content are the one exception (D7).

### D4 — `<details>`

`DetailsBlock : ContainerBlock` carries `Summary` (a `ContainerInline`, possibly empty), `IsOpen` (the `open` attribute) and `Ordinal` (its pre-order index among the document's details blocks, assigned by the pass).

**Matching.** Within one block container, the `details` open and close tags of *every* sibling `HtmlBlock` — rejected ones too — are matched in document order with a stack. Matching never depends on eligibility, so a rejected block cannot change which tags pair. A pair is matched only within one parent container: a `<details>` opened in a list item and closed outside it is two unmatched tags.

**Rejection.** A pair is rejected when its nesting is deeper than 8 or the depth budget refuses its fold. A block is rejected by D3's own conditions, or when it holds an unmatched `details` tag, or a tag of a rejected pair, or a tag whose partner's block is rejected. The last clause propagates — rejecting a block rejects the partner blocks of all its `details` tags — and runs to a fixpoint from a worklist before anything mutates. Each block is rejected at most once.

So in

```text
<details><summary>Outer</summary>

<details><span>unsupported</span>

inner body

</details>

outer tail

</details>
```

the inner opener is rejected for `<span>`, its partner `</details>` block is rejected with it, and the outer pair folds around all of it: the two rejected blocks render as source inside the outer details. And a block holding `</details><details>` whose new opener is unmatched rejects the block, which rejects the opener block of the pair it closes.

**Folding.** For a surviving pair, everything between the tags moves into the `DetailsBlock`: the converted content of the opening and closing blocks that lies inside the pair, and every sibling block between them, rejected or not, which Markdig has already parsed. Content of those two blocks outside the pair stays outside. A pair entirely inside one `HtmlBlock` (no blank line after the summary) folds the same way; its content is HTML, not markdown, as on github.com.

**Summary.** A `summary` element is accepted only as the first thing after its `<details>` open tag, whitespace and comments aside, in the same block, once. A `summary` anywhere else — with no `details` owner in its block, a second one, one after other content — rejects its block. Its content allows inline tags only. Links do not survive in a summary: `a` contributes its children and `img` its label (D6) as plain inlines, because the header is a button and a press on it toggles. An absent or empty summary renders as the word "Details".

**Rendering.** A collapsed details section renders its header and nothing else: its content is not in the visual tree, so it is not selectable, not copied by select-all, and not hit-tested, which is how github.com treats it too. Rendering the content hidden is not an option, because the selection index ignores visibility: hidden text would be copied, and its stale bounds would take clicks meant for what follows.

- `DetailsExtension : IMarkViewExtension` is owned by the view and holds its details state: a map from `Ordinal` to expanded, empty by default so that `IsOpen` decides. It registers `DetailsBlockRenderer`, which writes a `Border.markdown-details` holding a `StackPanel`: a `ToggleButton.markdown-details-summary` (a chevron and the summary inlines, `IsChecked` = expanded, `Tag` = the ordinal) and, only when expanded, a content `StackPanel` written with `Push` / `WriteChildren` / `Pop`. Nested details are therefore rendered only inside an expanded parent.
- A toggle records the new state and re-renders the view: `_viewer.Markdown = null`, then the text again, in the same dispatcher turn. The re-render rebuilds the selection index, so it always matches what is visible. It clears any selection. When the toggled header had keyboard focus, focus returns to the header with the same ordinal.
- The state is cleared whenever `Text` or `Flavor` changes.
- Not an `Expander`: its content is outside the selection walk. The summary text itself is not selectable, being inside a button; the header takes focus and toggles on Space and Enter.

### D5 — `<pre>`

`HtmlPreBlock : LeafBlock` whose `Inline` holds the content's synthesized inlines. It is not mapped to a code block because a code block is plain text and `<pre>` content carries inline formatting — the bot comment above bolds inside it.

- Placement: `pre` is accepted only where nothing else is open in its block — at its top level, which includes directly inside a `details` pair — never inside an inline tag or a `summary`. Inside it only inline tags are accepted. Anything else rejects the block. A `pre` written mid-paragraph is not a block at all: Markdig parses `<b><pre>x</pre></b>` as a paragraph, where the inline rule converts the bold pair and leaves the `pre` tags as source.
- Whitespace is kept. The line end directly after `<pre>` is dropped, as HTML does. So is the one directly before `</pre>`: browsers do not show it as a line. Every other line end — `\r\n`, `\n`, or a decoded `&#10;` — becomes a hard `LineBreakInline`, and the text around it is split there.
- Code-like tags inside `pre` split the same way: `<pre><code>a\n\nb</code></pre>` is `CodeInline` `a`, two hard breaks, `CodeInline` `b`. An empty segment produces no node.

**Renderer.** A `Border` with classes `markdown-code-block` and `markdown-pre`, whose child is a `StackPanel` holding one `MarkdownSelectableTextBlock.markdown-pre-text`. A border around a panel around a selectable text block is the one shape both selection walkers index, so a `pre` stays selectable inside a list item and inside details in a list item — MarkView's own code-block shape, a bare `TextBlock` under the border, is not. It is also the text block type MarkView dispatches link clicks from (D8). The border class keeps the code-block frame; `markdown-pre-text` takes the monospace font and size the code-block text has, and a transparent background so that a press between glyphs still reaches it.

### D6 — Images, and one hyperlink at a time

In the GitHub flavor an image — `![alt](url)` and `<img>` alike — is a link labelled with its alt text. Chat keeps the source text.

- Label, from one function (`ImageLabel`, in `Capacitor.App.GitHubHtml`): the alt text — for a markdown image, the plain text of its children; else the last path segment of the URL, unescaped; else `image`. Whatever was chosen is then whitespace-collapsed and trimmed, and falls through to the next choice if that leaves it empty — the normalisation comes last because unescaping `a%0Ab.png` produces a newline.
- The label is made in the pass, not in the renderer: every image `LinkInline`, parsed or synthesized, has its children replaced by one `LiteralInline` holding its label, and a summary takes that literal in the image's place (D4). The label is then ordinary inline content, which is what lets D8 carry emphasis into it — `**![](https://h/name.png)**` keeps a bold `name.png`.
- The GitHub-mode image renderer writes the image's children: inside a policy hyperlink when the URL passes `LinkPolicy.IsOpenable`, plainly otherwise. An image the pass had to leave childless (D8) gets its `ImageLabel` written in their place, so an image always shows a label.

**No hyperlink inside a hyperlink**, in both flavors and for every kind: the link, autolink and image renderers created in one `Register` call share a `LinkScope`. A renderer that writes a hyperlink enters the scope for the duration of its children; inside the scope, a link writes its children and an autolink its URL text, as plain inlines. An image inside the scope writes its children — its label — plainly in the GitHub flavor. In the chat flavor an image is never a hyperlink, so the scope changes nothing for it: `[![badge](img)](target)` keeps the image's source text inside the outer link, as today. The outermost *openable* link wins: a link whose own target the policy refuses writes its children without entering the scope, so an openable link inside it still becomes a hyperlink. This covers `[![badge](img)](target)`, `<a><img></a>`, `<a href>[inner](url)</a>` and nested anchors alike.

`ImageLoaders` stays cleared in both flavors. Nothing is fetched on render: opening a pull request must never call a host its comments name.

### D7 — HTML comments are dropped where content converts

In the GitHub flavor a complete `<!-- … -->` renders nothing, as on github.com: as an inline, and as a token of a converting block. `<!-- marker -->Visible text` therefore renders `Visible text`. A comment inside a rejected block shows with the rest of that block's source — `<div><!-- m --></div>` stays as written — and an unterminated `<!--` is malformed, so its block shows as source. The chat flavor shows all comments, as today.

### D8 — Links are hoisted to where MarkView dispatches them

MarkView's dispatch reaches only a hyperlink that is a direct inline of an indexed `MarkdownSelectableTextBlock`. Rather than replace that dispatch, the pass puts every link where it works.

**Why not own the dispatch.** A lookup of our own needs the rectangle of a text range, and every route to one in Avalonia 12.1.2 is superlinear in the runs of a line: `TextLayout.HitTestTextRange` scans lines from the first on each call, and `TextLine.GetTextBounds` and `TextLine.GetCharacterHitFromDistance` both search the line's run index from zero for every run they pass. Avoiding them means rebuilding line geometry from glyph runs, bidi reordering included, to be run on every pointer move over text a stranger wrote. That is a text-geometry subsystem, and it is not what this issue is for.

**Normalisation.** Before the inline rule touches a leaf block's inline tree (D3 gives the order and why), everything in it that can become a hyperlink is made a `LinkInline` with renderable children: images get their label child (D6), and a CommonMark autolink (`<https://example.com>`, an `AutolinkInline` leaf) that is not an email address becomes a `LinkInline` to the same URL with that URL as its literal child, which the policy link renderer draws exactly as the autolink renderer would. Bare URLs already are `LinkInline`s. Replacing an image's existing children with one literal cannot deepen the tree; giving a child to an autolink or to a childless image (`![](u)`) can, and is refused past the depth budget. A refused autolink stays an `AutolinkInline` and renders through the autolink renderer as today. A refused image stays childless, and the GitHub-mode image renderer writes `ImageLabel` itself for an image that has no children, so its label is visible either way.

Neither of the two is hoisted. An `AutolinkInline` is not a `LinkInline`, so the rule below does not see it; and a childless image is excluded from it by name, because hoisting would lift it out of the emphasis its renderer-written label inherits — `**![](https://h/name.png)**` under deep block quotes must still read bold. Whether they activate follows from where they sit, not from how deep: the depth budget counts every container, block quotes and list items included, while MarkView's dispatch cares only about inline ancestry. With no inline container above it, either one is a direct inline of its text block and opens like any other link, through the policy and the command. Under emphasis it is drawn and inert.

**The rule.** Hoisting runs last for each leaf block's inline tree — parsed paragraphs, headings and table cells, and the paragraphs and `pre` blocks the block rule synthesized. Every `LinkInline` with children — markdown, autolink or synthesized, image or not — that sits under `EmphasisInline` ancestors is hoisted above them: `Emphasis[a, Link[b], c]` becomes `Emphasis[a]`, `Link[Emphasis[b]]`, `Emphasis[c]`, each copy keeping the original's delimiter, and an empty copy is dropped. The rendering is the same — the hyperlink's colour reaches its children, the emphasis still wraps the same text — and the hyperlink span is now a direct inline.

- Hoisting stops below an enclosing `LinkInline`: a link inside a link rises to be a direct child of the outer one, and D6 decides which of them becomes the hyperlink. When the policy refuses the outer target, its children are written straight into the paragraph, which makes the inner hyperlink a direct inline too.
- Only `EmphasisInline` ancestors are split, since those are the ones the pass knows how to copy. A link under any other container inline stays where it is.
- A link under more than 8 emphasis ancestors is not hoisted. Each hoist copies its ancestor chain twice at most — once inside the link, once around what followed it — so the cap bounds what one comment can make the pass create at 16 nodes per link; without it the factor is twice `MaxDepth`.
- Hoisting moves nodes sideways and never deepens the tree, so the depth budget is untouched.

A link left under an inline container — past the cap, under a container that is not emphasis, or one of the two refused forms above — is drawn as a link and does nothing, which is what chat does today for `**[x](url)**`.

`MarkdownView` keeps forwarding `LinkClicked` to the `OpenLink` command exactly as it does now. `<pre>` links work because D5 renders into a `MarkdownSelectableTextBlock`, and a summary has no links (D4).

**Accepted limits of the library's dispatch**, the same in chat today, each to be reported upstream rather than worked around here:

- It resolves a press to the nearest caret stop, so the trailing half of a link's last glyph falls outside the link, and with two links set against each other it falls into the second one. Both are policy-checked web links, and the press is on their shared edge.
- Its hover lookup runs on every pointer move and is not linear. It goes through `TextLayout.HitTestPoint`, which calls the same `TextLine.GetCharacterHitFromDistance` named above, so a pointer late in a line of R distinct runs costs on the order of R², and a scan of the block's inlines follows. A comment can choose R by alternating formatting over zero-width characters. This is not new: markdown emphasis reaches it the same way in the reader and in chat today, and HTML formatting only spells the same runs differently. What it costs is a sluggish pointer over that one line. Suppressing the library's hover handling would remove it, together with the hand cursor over every link for everyone; that trade is not taken here. The defect is Avalonia's run-index search, and that is where the report goes.

Keyboard activation of links stays as kcap-cli#842 describes it. Links inside formatting in the chat flavor stay inert; chat has no pass to hoist them, and giving it one is its own change.

### D9 — Styles

`MarkdownStyles.axaml` gains app-palette styles, scoped under `mv|MarkdownViewer` like the rest: `Border.markdown-details`, `ToggleButton.markdown-details-summary`, `:is(TextBlock).markdown-pre-text` (the `:is` form, since the text block is a `TextBlock` subclass), and the alert classes MarkView emits (`markdown-alert`, `markdown-alert-note` … `markdown-alert-caution`, `markdown-alert-header`), which the default pipeline never produced.

## Out of scope

Shown as source for now, recorded on kcap-cli#982 as follow-ups: `<table>`, `<p>`, `<div>`, `<span>`, headings and lists written as HTML, a `<summary>` in a separate block from its `<details>`, `@mentions`, `#123` and commit references, `suggestion` fences, `<picture>`, loading images. `ImageResizeMode` and `width`/`height` are moot while nothing loads.

Also not here, each its own issue: links inside formatting in the chat flavor, and the two limits of MarkView's link dispatch that D8 accepts, which go upstream.

## Error handling

The pass runs inside `Markdown.Parse`, on the UI thread, for every render, over text a stranger wrote. It is written to be total: the tokenizer has no failure mode, the mapping reads attributes that may be absent, and every rejection — malformed input, nesting, depth, details propagation — is decided before the container mutates, which then happens as whole-node replacements, so the tree is valid between any two steps.

A defect in the pass must still not take the window down with a comment. The `DocumentProcessed` handler catches at its boundary, writes one line to stderr and stops converting; what it had not reached renders as source. That catch cannot reach the renderer, which runs later; the depth budget is what keeps the renderer's nesting limit out of an author's hands, and the view tests below render the adversarial shapes rather than only parsing them.

## Testing

**Tree-level** (`test/Capacitor.App.Tests.Unit/GitHubHtml/`, no Avalonia session, parallel): parse with the GitHub pipeline and assert the exact node tree, source tags included. Whether a fixture is an `HtmlBlock` or a paragraph decides which rule it exercises, and CommonMark's start conditions make that easy to get wrong, so every block fixture first asserts its shape under a pipeline without the pass — the `HtmlBlock`s the case assumes — and only then its normalisation.

- Each mapping row, attribute quoting in its three forms, tag-name case.
- Inline pairing: `<b><i>x</i></b>`; `<b><i>x</b>y</i>` (bold around `<i>x`, then `y</i>`); `<i><b><i>x</b>y</i>` (the outer italic survives); a pair split across emphasis; `prefix <pre>x</pre> suffix` and `prefix <details><summary>S</summary>x</details> suffix` stay source; `<b><pre>x</pre></b>`, a paragraph, converts the bold and leaves the `pre` tags source.
- Block rule, each fixture opening with a tag alone on its first line so that it is a block: one unknown tag rejects the block and the same block without it converts; crossing tags with equal open and close counts reject; an open tag left open at the end of the block (`<b>`, then `x`) rejects; a close tag with nothing open rejects; `<summary>S</summary>` alone rejects; a second summary rejects; a `pre` between a `<b>` line and a `</b>` line rejects; `<details><b>x</details>y</b>` rejects, and that rejection reaches D4's propagation.
- Tag classes: a block that is just `</details>`, closing a pair, passes the block rule and folds; `<img>` and `<br>` inside a `summary` and inside a `pre` are accepted and leave nothing open; `<br/>` and `<img … />` behave as their bare forms; `</br>` and `</img>` reject a block and stay source in a paragraph; `<b/>x</b>` is bold `x`; `<b/>x` with no close rejects a block and stays source in a paragraph.
- Comments: a comment-only block is removed; `<!-- m -->Visible text` keeps `Visible text`; `<div><!-- m --></div>` is untouched; an unterminated comment block is untouched; an inline comment is removed.
- Details: markdown between blank lines folds the sibling blocks; details inside one block; nested; inside a list item and inside a block quote; no `</details>`; a stray `</details>`; opened in a list item and closed outside; the example of D4 with its exact tree; `</details><details>` with an unmatched opener rejecting the earlier pair; missing summary; `open`; ordinals; the nesting cap of 8.
- `<pre>`: whitespace, inner formatting and entities kept; the leading line end dropped; `<pre><code>a\n\nb</code></pre>` as code, two breaks, code; `&#10;` splitting; `\r\n`.
- No synthesized literal or code inline holds `\r` or `\n`, asserted over every case above.
- Links: `<a href="javascript:…">` yields a `LinkInline` the policy will refuse; `<a>` without `href`; a link and an image in a summary are plain inlines.
- Normalisation before hoisting, exact trees: `![alt](u)` and `<img>` hold one literal child, their label; `<https://example.com>` becomes a `LinkInline` to that URL whose child is the URL text; `<a@b.example>` stays an `AutolinkInline`.
- Hoisting, exact trees: `**a [b](u) c**` becomes bold `a `, a link holding bold `b`, bold ` c`; `**<https://example.com>**` and `<b><https://example.com></b>` become a link holding the bold URL; `**![badge](u)**` and `**![](https://h/name.png)**` become an image holding a bold label, `badge` and `name.png`; a link that is the emphasis's only child leaves no empty copies; `<b><i><a>` nests both copies inside the link with their delimiters kept; an image under emphasis hoists like a link; a link inside a link rises to the outer link's children and no further; a link under 9 emphasis ancestors stays put; 2 000 sibling links under 8 nested `<b>` create no more than 16 nodes each; the tree is no deeper after hoisting than before.
- Depth, the two fallbacks pinned apart: 200 nested `<b>` pairs in a paragraph convert pair by pair up to the budget and leave the rest as source tags; the same 200 in one HTML block reject the whole block, which stays an untouched `HtmlBlock`; in both, no node is deeper than `MaxDepth`.
- Depth and normalisation, under HTML wrappers: a paragraph of 200 `<b>` openers, then `<https://example.com>`, then 200 closers, and the same around `![](https://h/name.png)` and around `<img src="https://h/name.png">`. The centre is shallow when it is normalised — the wrappers are still sibling tags then — so all three normalise, the inline rule converts one pair fewer than it does around plain text, and no node is deeper than `MaxDepth`.
- Depth and normalisation, refused: nested block quotes put a paragraph's inlines at `MaxDepth` in the parsed tree. There the autolink stays an `AutolinkInline`, `![](https://h/name.png)` stays childless and `<img>` stays a source tag; one quote level fewer and all three normalise. The same refusal with one parsed emphasis ancestor, `**![](https://h/name.png)**`, leaves the childless image inside its bold, unhoisted. The view tests render the refused shapes: the URL text and `name.png` are on screen; the bare autolink in the quotes, a direct inline, dispatches its URL on a leading-edge click; the emphasised refused image reads `name.png` in bold.
- Image labels: alt; empty alt falling to the file name; `a%0Ab.png` and `a%0D%0Ab.png` yielding a label with no `\r` or `\n`; a file name that is only escaped whitespace falling to `image`; the same through a summary.
- Work: 50 000 unmatched `<i>` open tags followed by one `</b>` finishes within a bound that a quadratic walk would miss by orders of magnitude.
- Totality: generated tag soup (random interleavings of inline, block-only, unknown, unclosed and truncated tags) neither throws nor reaches the boundary catch, and a walk of every resulting tree finds no node deeper than `MaxDepth`.
- The tokenizer on truncated input (`<a href="`, `<`, `</`, `<!--`) returns text, sets `Malformed` where specified and does not throw.

**Headless view tests** (beside `MarkdownViewTests`, `[NotInParallel("AvaloniaSession")]`):

- The comment from the Problem section renders in the GitHub flavor with no `<` in any text block, the alt text as a link, `Name the repo's projects` bold-italic inside a code-block border, and a collapsed details section.
- Details and selection, through the viewer's `SelectAll` + `GetSelectedText`: collapsed by default, the content is absent from the copy; expanded, present; collapsed again, absent; a nested details inside an expanded parent follows the same three steps; `open` starts expanded.
- A link in a paragraph *after* a collapsed details, and after an expanded-then-collapsed one, opens its own URL on click.
- The header toggles on click and on Space, and keeps keyboard focus across the re-render; changing `Text` resets the state.
- Links open through the `OpenLink` command, asserted by the URL dispatched on a simulated click near the leading edge of the link's first glyph, as the existing click test does: `**[x](u)**` and `<b><i><a>` in the GitHub flavor, `**<https://example.com>**`, an emphasised image with an explicit label and one with a file-name label — each also asserted to render bold — a link inside `<pre>` on a line after a line break, a link after an emoji in the same paragraph, an image link, a link inside a list item and inside expanded details. Each dispatches exactly once.
- Every hyperlink the GitHub flavor renders for those inputs is a direct inline of a `MarkdownSelectableTextBlock`.
- An image with an empty alt and `%0A` or `%0D%0A` in its file name, standalone and inside a link, lays out under a height-unconstrained parent and no `Run` holds `\r` or `\n`.
- Source fallback, in both flavors: an unknown tag and an unmatched allow-listed tag, each spanning two lines inside a paragraph (`<span\n title="x">`, `<b\n>`), lay out under a height-unconstrained parent, no `Run` holds `\r` or `\n`, and the text blocks' combined text still reads as the source.
- A multi-line `pre` inside a list item, and inside an expanded details in that list item, is present in `SelectAll` + `GetSelectedText`.
- One hyperlink at a time, asserted on hyperlink ancestry and on the dispatched URL: an image in a link, a markdown link in `<a>`, nested `<a>`, and an openable link inside a refused one.
- A press on a link in a summary toggles and dispatches nothing.
- Multi-line `<pre><code>` with a blank line and a decoded `&#10;` lays out under a height-unconstrained parent, no `Run` holds a newline, and the selected text keeps the lines.
- 200 nested `<b>` pairs and 50 nested `<details>` render without throwing.
- A GitHub-flavor render leaves `ImageLoaders` empty; an alert block renders with the app's brushes.
- The existing chat-flavor tests pass unchanged, and new ones pin under `Flavor="Chat"` that an HTML comment, an image and `<details>` render as source, and that `[![badge](img)](target)` is one hyperlink to the target whose text is the image's source form.
- Changing `Flavor` on a live view re-renders its current text.

## Risks

- **The re-render on toggle.** It rests on `Markdown = null` followed by the text rendering twice in one dispatcher turn, with no frame between. If the viewer ever defers its render, the toggle flickers; the view test for focus and visible state would still pass, so this one is checked by eye when the renderer lands.
- **The library's link dispatch.** Activation stays MarkView's, with the two limits D8 accepts: a press on a link's trailing edge can miss or land next door, and hovering a line with very many runs is quadratic in them. Both exist today in the reader and in chat, and both are fixed upstream or not at all; this design neither adds a way to reach them nor removes one.
- **Markdig's HTML block boundaries.** Which lines land in which `HtmlBlock` follows CommonMark's seven start conditions, and the fold depends on it. The tree-level tests fix the shapes this design relies on, so a Markdig upgrade that changes them fails a test rather than a render.
