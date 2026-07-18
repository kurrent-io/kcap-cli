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

# Pathspecs need `:(glob)` magic: git's default (non-magic) pathspec matching
# for a `**` pattern only matches files nested at least one directory below
# the anchor and silently excludes files sitting directly in src/ or test/
# (verified empirically -- 'src/**/*.cs' misses a top-level src/Foo.cs but
# finds src/Commands/Nested.cs; ':(glob)src/**/*.cs' finds both).
# git grep exit codes: 0 = matches, 1 = no matches, >1 = error.
matches="$(git grep -n -E "$ID_RE" -- ':(glob)src/**/*.cs' ':(glob)test/**/*.cs')"
rc=$?
[ "$rc" -gt 1 ] && { echo "::error::git grep failed (exit $rc) — treating as failure." >&2; exit 2; }
[ "$rc" -eq 1 ] && exit 0   # no AI-\d+ tokens anywhere in scope

# grep -v exit codes: 0 = something remains (violation), 1 = all filtered
# (every hit was correctly suppressed), >1 = error.
violations="$(printf '%s\n' "$matches" | grep -vE "$SUPPRESS_RE")"
grc=$?
[ "$grc" -gt 1 ] && { echo "::error::grep failed (exit $grc) applying the suppression filter — treating as failure." >&2; exit 2; }
[ "$grc" -eq 1 ] && exit 0  # every hit was on a correctly-suppressed line

echo "$violations"
echo "::error::Linear issue ID token found in tracked C# source. Rewrite per docs/superpowers/specs/2026-07-17-ai1392-linear-id-comment-sweep-design.md. Suppression (// linear-id-ok: <reason>) is for synthetic ID-shaped test data ONLY — a genuine internal reference must be escalated to an owner before merge, never suppressed."
exit 1
