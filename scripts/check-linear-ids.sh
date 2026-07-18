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

# Suppression (// linear-id-ok: <reason>) may ONLY drop a test/ hit — it exists
# for synthetic ID-shaped test data, never for a genuine internal reference.
# Any hit under src/ is ALWAYS a violation, suppression marker or not: partition
# matches by path first, then apply the suppression filter to the test/ half
# only. (git grep prefixes each hit with the pathspec-relative path, so a
# leading `src/` / `test/` on the match line is exactly the partition key.)
# grep exit codes throughout: 0 = something matched, 1 = nothing matched
# (not an error — just an empty partition/empty remainder), >1 = error.
src_matches="$(printf '%s\n' "$matches" | grep -E '^src/')"
src_rc=$?
[ "$src_rc" -gt 1 ] && { echo "::error::grep failed (exit $src_rc) partitioning src/ matches — treating as failure." >&2; exit 2; }

test_matches="$(printf '%s\n' "$matches" | grep -E '^test/')"
test_rc=$?
[ "$test_rc" -gt 1 ] && { echo "::error::grep failed (exit $test_rc) partitioning test/ matches — treating as failure." >&2; exit 2; }

test_violations=""
if [ "$test_rc" -eq 0 ]; then
    test_violations="$(printf '%s\n' "$test_matches" | grep -vE "$SUPPRESS_RE")"
    tv_rc=$?
    [ "$tv_rc" -gt 1 ] && { echo "::error::grep failed (exit $tv_rc) applying the suppression filter — treating as failure." >&2; exit 2; }
    [ "$tv_rc" -eq 1 ] && test_violations=""  # every test/ hit was correctly suppressed
fi

# src/ hits are unconditional violations (no suppression filter applied above);
# test/ hits only survive if grep -v didn't strip them as suppressed.
if [ "$src_rc" -eq 0 ] && [ -n "$test_violations" ]; then
    violations="$(printf '%s\n%s' "$src_matches" "$test_violations")"
elif [ "$src_rc" -eq 0 ]; then
    violations="$src_matches"
else
    violations="$test_violations"
fi

[ -z "$violations" ] && exit 0

echo "$violations"
echo "::error::Linear issue ID token found in tracked C# source. Rewrite per docs/superpowers/specs/2026-07-17-ai1392-linear-id-comment-sweep-design.md. Suppression (// linear-id-ok: <reason>) is for synthetic ID-shaped test data under test/ ONLY — any hit under src/ is always a violation regardless of the marker, and a genuine internal reference must be escalated to an owner before merge, never suppressed."
exit 1
