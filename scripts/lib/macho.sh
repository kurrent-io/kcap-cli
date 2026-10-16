#!/usr/bin/env bash
# Prints every Mach-O regular file under <dir>, deepest path first, so a caller signing them
# signs inside-out. Found by content: a bundle's Mach-Os do not share a name pattern.
macho_files() {
  find "$1" -type f | while IFS= read -r f; do
    if file -b "$f" | head -n 1 | grep -q 'Mach-O'; then
      printf '%s\t%s\n' "$(tr -cd / <<<"$f" | wc -c | tr -d ' ')" "$f"
    fi
  done | sort -t$'\t' -k1,1nr -k2,2 | cut -f2-
}

# Fails on a symlink under <dir> that resolves outside it or dangles. macho_files never follows a
# link, so a link out of the tree would ship pointing at code nothing signed.
contained_symlinks() {
  local root link target
  root="$(cd "$1" && pwd -P)"
  while IFS= read -r link; do
    if ! target="$(realpath "$link" 2>/dev/null)" || [ ! -e "$target" ]; then
      echo "$link is a dangling symlink" >&2; return 1
    fi
    case "$target" in
      "$root"/*) ;;
      *) echo "$link points outside the bundle, at $target" >&2; return 1 ;;
    esac
  done < <(find "$1" -type l)
}
