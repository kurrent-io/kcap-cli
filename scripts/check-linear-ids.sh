#!/usr/bin/env bash
# scripts/check-linear-ids.sh — see docs/superpowers/specs/2026-07-17-ai1392-linear-id-comment-sweep-design.md
# Exit: 0 clean, 1 = violation found, 2 = checker failure (never "clean").
# Portable POSIX ERE only — no PCRE, no `grep -P`, no `git grep -P` anywhere in
# this script. Verified on BSD grep (macOS stock /usr/bin/grep, BSD grep
# 2.6.0-FreeBSD, which rejects -P outright with "invalid option -- P", exit 2)
# and GNU grep (Linux CI runners) — see Guard design below for the exact test
# matrix. This is what makes "run identically on a laptop" actually true.
set -u

# Word-bounded AI-<digits> without \b (POSIX ERE has none): a left guard that
# rejects a directly-preceding identifier char, and a right guard that rejects
# a directly-following one. Both character classes are IDENTICAL — letters,
# digits, and `_` only, no hyphen on either side — matching \b's word/non-word
# transition exactly (a hyphen is a non-word character in \w/\b semantics, so
# a hyphen-preceded token like `prefix-AI-1382` has a real boundary and must
# match; excluding hyphen from the left guard was a bug, not a deliberate
# widening — see the case table's hyphen-preceded rows).
ID_RE='(^|[^A-Za-z0-9_])AI-[0-9]+($|[^A-Za-z0-9_])'
SUPPRESS_RE='//[[:space:]]*linear-id-ok:[[:space:]]*[^[:space:]]'

# src/ and test/ are each checked with their OWN pathspec-scoped `git grep`
# invocation, rather than one combined grep whose DISPLAY output is then
# reparsed for a leading `src/`/`test/` prefix to decide which hits may be
# suppressed. `git grep`'s presentation output is not a stable data format
# to reparse: it can carry ANSI color escapes (if color leaks in from
# user/CI config, e.g. a `color.grep=always` in a global gitconfig) and,
# independent of color, git C-quotes any path containing whitespace or
# non-ASCII bytes in double quotes with backslash escapes (e.g. a file
# literally named `src/weird name.cs` prints as `"src/weird name.cs"`). A
# genuine src/ violation living at such a path could therefore begin with
# an escape byte or a literal `"` rather than `s`, matching NEITHER a
# `^src/` nor a `^test/` regex against the combined display text — silently
# dropped from both partitions, and the checker would exit 0 despite an
# in-scope violation. This was a real fail-open bug in an earlier revision
# of this script (see Guard design below for the empirical regression case
# that proves it and the fix).
#
# Scoping by pathspec instead means git itself resolves which tree a match
# came from — no path string is ever parsed, quoted or not, to make the
# src-vs-test decision, so this class of bug cannot recur.
#
# Pathspecs need `:(glob)` magic (unchanged from before): git's default
# (non-magic) pathspec matching for a `**` pattern only matches files
# nested at least one directory below the anchor and silently excludes
# files sitting directly in src/ or test/ (verified empirically —
# 'src/**/*.cs' misses a top-level src/Foo.cs but finds
# src/Commands/Nested.cs; ':(glob)src/**/*.cs' finds both).
#
# `--no-color` on both invocations forecloses the ANSI-escape half of the
# bug outright (belt-and-braces — pathspec scoping already means neither
# invocation's output is reparsed for a path prefix, so nothing depends on
# `--no-color`, but it costs nothing and removes the escape-sequence risk
# from the picture entirely rather than relying solely on "we don't parse
# this anyway").
#
# git grep exit codes throughout: 0 = matches, 1 = no matches (not an
# error — just an empty result for that tree), >1 = error. Each invocation
# reports and is checked against its OWN status; a failure in either one
# fails closed (exit 2), never falls through to "clean".

src_matches="$(git grep --no-color -nE "$ID_RE" -- ':(glob)src/**/*.cs')"
src_rc=$?
[ "$src_rc" -gt 1 ] && { echo "::error::git grep failed (exit $src_rc) scanning src/ — treating as failure." >&2; exit 2; }

test_matches="$(git grep --no-color -nE "$ID_RE" -- ':(glob)test/**/*.cs')"
test_rc=$?
[ "$test_rc" -gt 1 ] && { echo "::error::git grep failed (exit $test_rc) scanning test/ — treating as failure." >&2; exit 2; }

# src/ hits are UNCONDITIONAL violations: suppression may NEVER apply there,
# so there is no filter to run — a match in src/ is a violation, full stop.
src_violations=""
[ "$src_rc" -eq 0 ] && src_violations="$src_matches"

# test/ hits are eligible for suppression: a line matching SUPPRESS_RE (the
# `// linear-id-ok: <reason>` marker) is dropped; whatever remains is a
# violation. The marker match is against line CONTENT only — the src-vs-test
# decision was already made above by which pathspec-scoped grep produced the
# line, not by inspecting the line's leading path text.
#
# The filter only runs when test_rc is 0 (i.e. test_matches is guaranteed
# non-empty — at least one real match line). This sidesteps a genuine
# empty-input edge case in `grep -v`: piping a truly empty (0-byte) stream
# into `grep -vE` exits 1 with no output (verified empirically — see Guard
# design), which already reads as "no violations" and is NOT an error, but
# gating on test_rc means production behavior never has to depend on that
# nuance in the first place — when there's nothing to filter, we simply
# don't invoke the filter.
test_violations=""
if [ "$test_rc" -eq 0 ]; then
    test_violations="$(printf '%s' "$test_matches" | grep -vE "$SUPPRESS_RE")"
    tv_rc=$?
    [ "$tv_rc" -gt 1 ] && { echo "::error::grep failed (exit $tv_rc) applying the suppression filter to test/ matches — treating as failure." >&2; exit 2; }
    [ "$tv_rc" -eq 1 ] && test_violations=""  # every test/ hit was correctly suppressed
fi

# Aggregate: any src/ hit (always a violation) plus any unsuppressed test/
# hit. Both are independently gathered above by pathspec, not by reparsing
# combined output — the two invocations' results are simply concatenated
# here for reporting.
if [ -n "$src_violations" ] && [ -n "$test_violations" ]; then
    violations="$(printf '%s\n%s' "$src_violations" "$test_violations")"
elif [ -n "$src_violations" ]; then
    violations="$src_violations"
else
    violations="$test_violations"
fi

[ -z "$violations" ] && exit 0

echo "$violations"
echo "::error::Linear issue ID token found in tracked C# source. Rewrite per docs/superpowers/specs/2026-07-17-ai1392-linear-id-comment-sweep-design.md. Suppression (// linear-id-ok: <reason>) is for synthetic ID-shaped test data under test/ ONLY — any hit under src/ is always a violation regardless of the marker, and a genuine internal reference must be escalated to an owner before merge, never suppressed."
exit 1
