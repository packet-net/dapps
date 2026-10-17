#!/usr/bin/env python3
"""Run one xunit v3 test binary for CI, rerunning only the tests that failed.

    scripts/ci-test.py <label> <test-binary> <attempts> [filter args...]

Attempt 1 runs everything the filter args select. Each later attempt reruns
only the tests that failed on the attempt before (by method name, from the
xunit XML report), never the whole suite again (#331). How many attempts a
failed test gets depends on how it failed, read from tags in its failure
message (dapps.core.tests/Integration/ScenarioTiming.cs):

  [over-ceiling]  a scenario over twice its time target, or its messages
                  never arrived: failed for good, never rerun.
  [over-target]   a scenario over its time target: rerun once; a second
                  failure fails the job.
  anything else   rerun until <attempts> attempts in all.

A test that needed a rerun to pass doesn't fail the job, but it is never
silent: its name goes into the job summary and a warning annotation. A run
that exits non-zero without naming a failed test (a crash, a fixture error,
no tests found) is not rerun: there is nothing to narrow the rerun to.

Reports land in <binary dir>/TestResults/<label>-attempt<N>.xml.
"""
import os
import subprocess
import sys
import time
import xml.etree.ElementTree as ET

OVER_TARGET = "[over-target]"
OVER_CEILING = "[over-ceiling]"


def summary(text):
    path = os.environ.get("GITHUB_STEP_SUMMARY")
    if path:
        with open(path, "a", encoding="utf-8") as f:
            f.write(text + "\n")


def annotate(kind, title, message):
    print(f"::{kind} title={title}::{message}", flush=True)


def read_report(path):
    """(failed tests as {name: failure message}, report errors), or None if unreadable."""
    try:
        root = ET.parse(path).getroot()
    except Exception:
        return None
    failed = {}
    for t in root.iter("test"):
        if t.get("result") == "Fail":
            name = f"{t.get('type')}.{t.get('method')}"
            message = t.findtext("failure/message") or ""
            failed[name] = failed.get(name, "") + message
    errors = []
    for e in root.iter("error"):
        message = (e.findtext("failure/message") or "").strip().splitlines()[:3]
        errors.append(f"{e.get('type')}: {e.get('name')}: {' '.join(message)}")
    return failed, errors


def bullets(names):
    return "\n".join(f"- `{n}`" for n in names)


def main():
    label, binary, attempts = sys.argv[1], sys.argv[2], int(sys.argv[3])
    filters = sys.argv[4:]
    results = os.path.join(os.path.dirname(binary), "TestResults")
    os.makedirs(results, exist_ok=True)

    failed_for_good = {}    # name -> why
    ever_failed = []        # in first-failure order
    over_target_misses = {}
    rerun = None            # None: everything the filters select
    for attempt in range(1, attempts + 1):
        report = f"{label}-attempt{attempt}.xml"
        args = filters if rerun is None else [a for n in rerun for a in ("--filter-method", n)]
        print(f"::group::{label}: attempt {attempt} of {attempts} ({' '.join(args)})", flush=True)
        code = subprocess.call([binary, *args, "--output", "detailed", "--no-progress",
                                "--results-directory", results,
                                "--report-xunit", "--report-xunit-filename", report])
        print("::endgroup::", flush=True)
        if code == 0:
            break

        parsed = read_report(os.path.join(results, report))
        failed, errors = parsed if parsed else ({}, ["(no readable report: the test run did not finish)"])
        if not failed:
            annotate("error", label, f"the test run exited with code {code} without naming a failed test; not rerunning")
            print("\n".join(errors) or "no errors in the report")
            summary(f"### {label}: failed (exit code {code}, no failed test named)\n\n```\n"
                    + ("\n".join(errors) or "no errors in the report") + "\n```")
            return 1

        rerun = []
        for name, message in sorted(failed.items()):
            if name not in ever_failed:
                ever_failed.append(name)
            if OVER_CEILING in message:
                failed_for_good[name] = "over its hard time ceiling, or its messages never arrived (never rerun)"
            elif OVER_TARGET in message:
                over_target_misses[name] = over_target_misses.get(name, 0) + 1
                if over_target_misses[name] >= 2:
                    failed_for_good[name] = "over its time target twice"
                else:
                    rerun.append(name)
            elif attempt == attempts:
                failed_for_good[name] = f"failed on all {attempts} attempts"
            else:
                rerun.append(name)
        print(f"{label}: attempt {attempt}: {len(failed)} failed:")
        for name in sorted(failed):
            print(f"  {name}")
        if rerun and attempt < attempts:
            annotate("warning", f"{label}: rerunning", f"attempt {attempt} failed: {' '.join(rerun)}")
            time.sleep(5)
        else:
            for name in rerun:
                failed_for_good.setdefault(name, f"failed on all {attempts} attempts")
            break

    healed = [n for n in ever_failed if n not in failed_for_good]
    for name in healed:
        why = "missed its time target" if name in over_target_misses else "failed"
        annotate("warning", f"{label}: flaky test", f"{name} {why}, then passed when rerun on its own")
    if healed:
        summary(f"### {label}: passed only on a rerun\n\nThese tests failed (or missed a scenario time target) "
                "and then passed when rerun on their own. Flaky at best, a race at worst; the first attempt's "
                "log says why:\n\n" + bullets(healed))
    if failed_for_good:
        for name, why in failed_for_good.items():
            annotate("error", f"{label}: failed", f"{name}: {why}")
        summary(f"### {label}: failed\n\n" + "\n".join(f"- `{n}`: {w}" for n, w in failed_for_good.items()))
        return 1
    print(f"{label}: passed.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
