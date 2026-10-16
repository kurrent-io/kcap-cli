#!/usr/bin/env bash
# Prints every Mach-O regular file under <dir>, deepest path first, so a caller signing them
# signs inside-out. Found by content: a bundle's Mach-Os do not share a name pattern. Symlinks
# are not followed; the file they point at is listed in its own right.
macho_files() {
  find "$1" -type f | while IFS= read -r f; do
    if file -b "$f" | head -n 1 | grep -q 'Mach-O'; then
      printf '%s\t%s\n' "$(tr -cd / <<<"$f" | wc -c | tr -d ' ')" "$f"
    fi
  done | sort -t$'\t' -k1,1nr -k2,2 | cut -f2-
}
