#!/usr/bin/env bash
# Diffs two decompiled game assemblies, filtering out the churn that carries no
# behavioural meaning.
#
# Usage: tools/decompile-diff.sh <old-decompile-dir> <new-decompile-dir> [file]
#
# With no third argument it prints the list of files that differ. With one, it
# prints the *real* diff of that file.
#
# Why the filtering matters. ilspycmd emits two things that renumber whenever
# anything earlier in the assembly shifts: //IL_ offset comments, and the
# compiler-generated names for async state machines and lambda display classes
# (_003C...__DisplayClass21_0 and friends). A one-line change in a method's
# middle therefore renames every state machine after it, and a plain diff of the
# file comes back hundreds of lines long with no real change in it.
#
# 0.110.1 is the case that motivated this: AutoSlayer.cs looked heavily reworked
# and was almost entirely renumbering over a single new bool field. Reading that
# by eye is how a real change in the next file gets skimmed past.
#
# What survives the filter is what actually changed. The filter is deliberately
# blunt - it drops whole lines mentioning generated names, so a real change to a
# line that also mentions one is invisible here. This narrows the question; it
# does not answer it. When a file matters, read it.
set -euo pipefail

OLD="${1:?usage: decompile-diff.sh <old-dir> <new-dir> [file]}"
NEW="${2:?usage: decompile-diff.sh <old-dir> <new-dir> [file]}"
FILE="${3:-}"

if [ -z "$FILE" ]; then
  # No filtering on the file list: an added or removed type is exactly the kind
  # of thing an "Only in" line reports, and those are read as carefully as the
  # "differ" lines (see CLAUDE.md).
  # diff exits 1 when files differ, which here is the ordinary result and not an
  # error - without the guard, set -e would abort on every patch that changed
  # anything.
  diff -rq "$OLD" "$NEW" || true
  exit 0
fi

strip() {
  grep -vE '^[[:space:]]*//IL_' "$1" \
    | grep -vE '_003C|__DisplayClass|IAsyncStateMachine|AsyncTaskMethodBuilder|_003Eu__|SetStateMachine' \
    | grep -vE '^[[:space:]]*(goto )?IL_[0-9a-f]+[:;]?$'
}

TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT

strip "$OLD/$FILE" > "$TMP/old.txt"
strip "$NEW/$FILE" > "$TMP/new.txt"
diff "$TMP/old.txt" "$TMP/new.txt" || true
