#!/usr/bin/env bash
# Raises a warning, and a job summary entry, for every simulated-radio
# scenario report that missed its time target (#352). The scenarios fail
# only past twice their target, so a miss alone passes; this keeps it seen.
#
#   scripts/scenario-targets.sh <scenario-reports dir>
set -uo pipefail
dir="$1"
marker="Over the time target"
summary="${GITHUB_STEP_SUMMARY:-/dev/null}"
[ -d "$dir" ] || { echo "no scenario reports in $dir"; exit 0; }
found=0
for f in "$dir"/*.md; do
  [ -e "$f" ] || continue
  while IFS= read -r line; do
    if [ "$found" -eq 0 ]; then
      printf '### Scenarios over their time target\n\nThese passed (the hard ceiling is twice the target) but took longer than they should:\n\n' >> "$summary"
    fi
    found=1
    text="$(printf '%s' "$line" | sed 's/\*\*//g')"
    echo "::warning title=Scenario over its time target::$(basename "$f"): $text"
    echo "- \`$(basename "$f")\`: $text" >> "$summary"
  done < <(grep -F "$marker" "$f")
done
[ "$found" -eq 0 ] && echo "Every scenario report is within its time target."
exit 0
