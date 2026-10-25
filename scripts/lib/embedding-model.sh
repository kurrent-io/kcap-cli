#!/usr/bin/env bash
# A bundle's embedding-model.json as kcap's LocalEmbeddingModelManifest.Parse reads it. kcap refuses a
# whole bundle manifest whose embedding_model entry it rejects, so a release must never sign one.

# Prints "absent" or "present" for the archive's ./embedding-model.json; fails when tar cannot list it.
embedding_model_member() { # <archive>
  local members
  members="$(tar -tf "$1")" || return 1
  if grep -qxF ./embedding-model.json <<<"$members"; then echo present; else echo absent; fi
}

# Succeeds when kcap would accept the file as an embedding_model entry.
embedding_model_valid() { # <file>
  jq -e '
    def hex64: type == "string" and test("^[0-9a-f]{64}$");
    def int($max): type == "number" and (tojson | test("^[0-9]+$")) and . > 0 and . <= $max;
    type == "object"
    and (.model_id | type == "string" and test("\\S"))
    and (.contract | int(2147483647))
    and (.model_set | hex64)
    and (.files | type == "array" and length > 0)
    and all(.files[]; type == "object"
                      and (.name | type == "string" and test("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$"))
                      and (.sha256 | hex64)
                      and (.size | int(9007199254740991)))
    and ([.files[].name | ascii_downcase] | length == (unique | length))' "$1" >/dev/null 2>&1
}
