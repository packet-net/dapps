#!/usr/bin/env bash
# The backticks in single quotes below are Markdown for the job summary, not expansions.
# shellcheck disable=SC2016
# Runs one xunit v3 test binary for CI and retries only the tests that failed.
#
#   scripts/ci-test.sh <label> <test-binary> <attempts> [filter args...]
#
# Attempt 1 runs everything the filter args select. If tests fail, each later
# attempt reruns only the tests that failed on the attempt before it (by
# method name, from the xunit XML report), never the whole suite again. A test
# that fails on every attempt fails the step. A test that needed a retry to
# pass does not fail the step, but it is never silent: its name goes into the
# job summary and into a warning annotation on the run.
#
# A run that exits non-zero without naming a failed test (a crash, a fixture
# cleanup error, no tests found) is not retried: there is nothing to narrow
# the rerun to, and rerunning everything is what this script exists to stop.
#
# Reports land in <binary dir>/TestResults/<label>-attempt<N>.xml for the
# artifact upload.
set -uo pipefail

label="$1"; binary="$2"; attempts="$3"; shift 3
results="$(dirname "$binary")/TestResults"
mkdir -p "$results"
summary="${GITHUB_STEP_SUMMARY:-/dev/null}"

# Fully qualified names of the failed tests in an xunit XML report, one per
# line, sorted and unique (a theory's rows share one method name).
failed_in() {
  python3 - "$1" <<'EOF'
import sys, xml.etree.ElementTree as ET
try:
    root = ET.parse(sys.argv[1]).getroot()
except Exception:
    sys.exit(0)
names = {f"{t.get('type')}.{t.get('method')}" for t in root.iter('test') if t.get('result') == 'Fail'}
print("\n".join(sorted(names)))
EOF
}

# Anything in the report's <errors> (fixture set-up or clean-up failures,
# which fail the run without failing a test).
errors_in() {
  python3 - "$1" <<'EOF'
import sys, xml.etree.ElementTree as ET
try:
    root = ET.parse(sys.argv[1]).getroot()
except Exception:
    print("(no readable report: the test run did not finish)")
    sys.exit(0)
for e in root.iter('error'):
    f = e.find('failure')
    msg = (f.findtext('message') or '').strip().splitlines()[:3] if f is not None else []
    print(f"{e.get('type')}: {e.get('name')}: {' '.join(msg)}")
EOF
}

filter=("$@")
declare -a first_failures=()
for i in $(seq 1 "$attempts"); do
  report="$label-attempt$i.xml"
  echo "::group::$label: attempt $i of $attempts${filter[*]:+ (${filter[*]})}"
  "$binary" "${filter[@]}" --output detailed --no-progress \
    --results-directory "$results" --report-xunit --report-xunit-filename "$report"
  code=$?
  echo "::endgroup::"
  if [ "$code" -eq 0 ]; then
    if [ "$i" -gt 1 ]; then
      {
        echo "### $label: passed only after a retry"
        echo
        echo "These tests failed on attempt 1 and passed when rerun on their own. Flaky at best, a race at worst; the logs of attempt 1 say why:"
        echo
        printf -- '- `%s`\n' "${first_failures[@]}"
      } >> "$summary"
      for t in "${first_failures[@]}"; do
        echo "::warning title=$label: flaky test::$t failed on attempt 1 and passed on a retry"
      done
    fi
    echo "$label: passed on attempt $i of $attempts."
    exit 0
  fi

  mapfile -t failed < <(failed_in "$results/$report" | sed "/^$/d")
  if [ "${#failed[@]}" -eq 0 ]; then
    echo "::error title=$label::the test run exited with code $code without naming a failed test; not retrying"
    errors="$(errors_in "$results/$report")"
    [ -n "$errors" ] && echo "$errors"
    {
      echo "### $label: failed (exit code $code, no failed test named)"
      echo
      echo '```'
      echo "${errors:-no errors in the report}"
      echo '```'
    } >> "$summary"
    exit 1
  fi
  [ "$i" -eq 1 ] && first_failures=("${failed[@]}")

  echo "$label: attempt $i: ${#failed[@]} failed:"
  printf '  %s\n' "${failed[@]}"
  if [ "$i" -lt "$attempts" ]; then
    echo "::warning title=$label: retrying::attempt $i failed: ${failed[*]}"
    filter=()
    for t in "${failed[@]}"; do filter+=(--filter-method "$t"); done
    sleep 5
  else
    {
      echo "### $label: failed on all $attempts attempts"
      echo
      printf -- '- `%s`\n' "${failed[@]}"
      healed=()
      for t in "${first_failures[@]}"; do
        printf '%s\n' "${failed[@]}" | grep -qxF "$t" || healed+=("$t")
      done
      if [ "${#healed[@]}" -gt 0 ]; then
        echo
        echo "Also failed on attempt 1, then passed on a retry:"
        echo
        printf -- '- `%s`\n' "${healed[@]}"
      fi
    } >> "$summary"
    for t in "${failed[@]}"; do
      echo "::error title=$label: failed::$t failed on all $attempts attempts"
    done
    exit 1
  fi
done
