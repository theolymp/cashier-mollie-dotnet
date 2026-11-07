#!/usr/bin/env python3
"""Decides whether this repository's weekly scan timer is still alive.

Why this exists: the scheduled trigger re-scans the *published* package against an advisory
database that moves while the code stands still. GitHub disables scheduled workflows in public
repositories after 60 days without repository activity -- so the timer can stop without anything
failing, and from the outside a dead timer and a clean scan look identical. "No alert" is not
"scanned".

Two things are asked here, and they are NOT the same kind of claim:

  state     the workflow's own `state` field. `disabled_inactivity` is a value the API returns,
            so the thing the caveat called silent is in fact *measured*. One fact, one fix.
  age       how long ago the newest scheduled run started. This is INFERENCE: GitHub delays
            scheduled runs under load (measured 2026-11-07: a `17 4 * * 1` cron started 12:28
            UTC that Monday, eight hours late), and the runs API only keeps a window. So age
            cannot carry a verdict -- it is reported with its base count and nothing else.

Exit code IS the tier, as in license-classify.py:

  0  ALIVE        state active, newest scheduled run inside the window
  1  UNCLEAR      state active, but the age cannot be read as healthy -- reported, not blocking
  2  NOT MEASURED unreadable payload, missing field, or a state string this script does not know
  3  DISABLED     a measured non-active state -- the timer is off and needs one command

Tier 2 is deliberately NOT a hard failure in the caller, unlike the licence gate where the same
number is one. There the measurement is local and deterministic, so an unmeasurable run means a
guard is missing. Here it depends on a remote API called from inside a run: making a transient 403
turn main red would make this detector the flake, for a timer that matters weekly rather than per
commit. Tier 3 does fail, because it is measured and one command fixes it.

Takes two JSON files rather than fetching: the fetch belongs to the shell (and to its curl
guards), and passing payloads in is what lets the self-test below drive every tier against the
real decision code instead of a copy of it.
"""
import datetime
import io
import json
import os
import sys
import tempfile

EXIT_ALIVE = 0
EXIT_UNCLEAR = 1
EXIT_NOT_MEASURED = 2
EXIT_DISABLED = 3

DEFAULT_MAX_AGE_DAYS = 10

# Every state the REST API documents for a workflow, each with the fix it actually needs -- the
# causes are different and so are the remedies, which is why they are not collapsed into one
# "disabled" message.
NON_ACTIVE_STATES = {
    "disabled_inactivity": (
        "GitHub switched the timer off after 60 days without repository activity. This is the "
        "exact failure this check exists for: the weekly re-scan stopped and nothing went red.\n"
        "   Fix: `gh workflow enable ci.yml` (or Actions -> CI -> Enable workflow). A commit that "
        "changes the `cron:` line also reactivates it."
    ),
    "disabled_manually": (
        "Someone disabled this workflow by hand. That may well be intended -- but while it holds, "
        "the published package is not being re-scanned.\n"
        "   Fix: `gh workflow enable ci.yml`, or record the decision in BACKLOG.md so the next "
        "reader is not left guessing."
    ),
    "disabled_fork": (
        "Scheduled workflows are off by default in a fork. Nothing to fix in the upstream "
        "repository; a fork that wants the timer enables it for itself."
    ),
    "deleted": (
        "The API reports this workflow as deleted, so there is no timer at all. Either the file "
        "moved and the caller still names the old path, or the workflow is gone."
    ),
}

# A `continue`-free sentinel: the self-test asserts the tool reports the *reason*, not just the
# tier, because a tier alone cannot tell a reader which of these four states fired.
_SELF_TEST_RUNNING = False


class NotMeasured(Exception):
    """Raised when the payload cannot answer the question. Never means 'alive'."""


def _require(payload, key, where):
    if not isinstance(payload, dict):
        raise NotMeasured("%s is not a JSON object, so it carries no fields to read" % where)
    if key not in payload:
        raise NotMeasured("%s has no '%s' field" % (where, key))
    return payload[key]


def _parse_timestamp(raw):
    # GitHub returns RFC 3339 with a literal Z. datetime.fromisoformat does not accept Z before
    # Python 3.11, and this runs on 3.9 as well, so parse the shape explicitly rather than
    # depending on the interpreter's tolerance.
    if not isinstance(raw, str):
        raise NotMeasured("the newest scheduled run's created_at is not a string")
    try:
        naive = datetime.datetime.strptime(raw, "%Y-%m-%dT%H:%M:%SZ")
    except ValueError:
        raise NotMeasured("created_at %r is not an RFC 3339 UTC timestamp" % raw)
    return naive.replace(tzinfo=datetime.timezone.utc)


def verdict(workflow, runs, now, max_age_days):
    """Returns (exit_code, lines). Raises NotMeasured when the payloads cannot answer."""
    lines = []
    path = workflow.get("path") if isinstance(workflow, dict) else None
    state = _require(workflow, "state", "the workflow payload")

    if state in NON_ACTIVE_STATES:
        lines.append("::error::TIMER OFF: %s is in state '%s'." % (path or "the workflow", state))
        lines.append("   " + NON_ACTIVE_STATES[state])
        return EXIT_DISABLED, lines
    if state != "active":
        raise NotMeasured(
            "the workflow state is %r, which this script does not know. Treated as unmeasured on "
            "purpose: an unrecognised state is not an 'active'." % state
        )

    total = _require(runs, "total_count", "the runs payload")
    if not isinstance(total, int):
        raise NotMeasured("total_count is %r, not a number" % total)
    listed = _require(runs, "workflow_runs", "the runs payload")
    if not isinstance(listed, list):
        raise NotMeasured("workflow_runs is not a list")

    if total == 0:
        lines.append(
            "::warning::The timer is active, but the API lists no scheduled run at all. That "
            "cannot be read as 'never fired': the runs API keeps a window, so an old enough run "
            "is indistinguishable from one that never happened. Nothing is concluded about the "
            "age here -- the state above is the measured part."
        )
        return EXIT_UNCLEAR, lines
    if not listed:
        raise NotMeasured(
            "total_count is %d but no run was listed, so the newest one cannot be read" % total
        )

    newest = listed[0]
    # The event filter is applied server-side. Verify it held rather than trusting it: a filter
    # that silently stops filtering returns push runs, which are fresh on every commit and would
    # make a dead timer read as a live one. That is the failure mode this whole file is about, so
    # it must not be reintroduced by the query.
    event = _require(newest, "event", "the newest run")
    if event != "schedule":
        raise NotMeasured(
            "the newest run's event is %r, not 'schedule' -- the event filter did not hold, and a "
            "push run says nothing about the timer" % event
        )

    started = _parse_timestamp(_require(newest, "created_at", "the newest run"))
    age_days = (now - started).total_seconds() / 86400.0
    if age_days < 0:
        raise NotMeasured(
            "the newest scheduled run started %.1f days in the future, so the clocks disagree and "
            "no age can be formed" % -age_days
        )

    shape = "%s, newest scheduled run %.1f days old (%s), %d scheduled run(s) in the API window" % (
        path or "the workflow", age_days, started.isoformat(), total,
    )
    if age_days > max_age_days:
        lines.append(
            "::warning::The timer is active but has not produced a run in %.1f days (window: %d)."
            % (age_days, max_age_days)
        )
        lines.append(
            "   Reported, not blocking: GitHub delays and drops scheduled runs under load, so an "
            "age is inference while the state above is measurement. Worth a look if it keeps "
            "growing -- Actions -> CI shows whether runs are being skipped."
        )
        lines.append("   Measured: " + shape)
        return EXIT_UNCLEAR, lines

    lines.append(">> Scan timer alive: " + shape + ".")
    lines.append(
        "   Measured, not assumed: state 'active' is read from the workflow API, where "
        "'disabled_inactivity' is its own value."
    )
    return EXIT_ALIVE, lines


def _read_json(path, where):
    if not os.path.exists(path):
        raise NotMeasured("%s (%s) does not exist" % (where, path))
    with open(path, "r", encoding="utf-8") as handle:
        raw = handle.read()
    if not raw.strip():
        raise NotMeasured("%s (%s) is empty, so the API returned nothing usable" % (where, path))
    try:
        return json.loads(raw)
    except ValueError as error:
        raise NotMeasured("%s (%s) is not readable JSON: %s" % (where, path, error))


def _write(payload):
    handle = tempfile.NamedTemporaryFile(
        mode="w", suffix=".json", delete=False, encoding="utf-8"
    )
    handle.write(payload if isinstance(payload, str) else json.dumps(payload))
    handle.close()
    return handle.name


def _runs(created_at, total=1, event="schedule"):
    return {
        "total_count": total,
        "workflow_runs": [{"event": event, "created_at": created_at}],
    }


def self_test():
    """Drives every tier through main(), asserting the tier AND what the output names.

    The tier alone is not the claim a reader acts on -- four different states share tier 3 and
    have four different fixes, so each case pins the phrase that tells them apart. A test that
    only compared exit codes would stay green with every message replaced by "failed".
    """
    global _SELF_TEST_RUNNING
    active = {"path": ".github/workflows/ci.yml", "state": "active"}
    fresh = "2026-11-02T12:28:30Z"
    now = "2026-11-07T10:00:00Z"
    failures = []

    for workflow, runs, wanted, says, case in (
        (active, _runs(fresh), EXIT_ALIVE,
         ("Scan timer alive", "4.9 days old", "1 scheduled run(s)", "disabled_inactivity"),
         "an active timer with a recent run"),
        (active, _runs("2026-09-01T04:17:00Z"), EXIT_UNCLEAR,
         ("::warning::", "has not produced a run in 67.2 days", "Reported, not blocking",
          "inference while the state above is measurement"),
         "an active timer whose newest run is far outside the window"),
        (active, {"total_count": 0, "workflow_runs": []}, EXIT_UNCLEAR,
         ("::warning::", "lists no scheduled run at all", "keeps a window",
          "Nothing is concluded about the age"),
         "an active timer with no run in the API window"),
        ({"path": "x", "state": "disabled_inactivity"}, _runs(fresh), EXIT_DISABLED,
         ("::error::TIMER OFF", "60 days without repository activity", "nothing went red",
          "gh workflow enable ci.yml"),
         "a timer GitHub switched off for inactivity"),
        ({"path": "x", "state": "disabled_manually"}, _runs(fresh), EXIT_DISABLED,
         ("::error::TIMER OFF", "by hand", "record the decision in BACKLOG.md"),
         "a timer someone disabled by hand"),
        ({"path": "x", "state": "disabled_fork"}, _runs(fresh), EXIT_DISABLED,
         ("::error::TIMER OFF", "off by default in a fork"),
         "a fork's default"),
        ({"path": "x", "state": "deleted"}, _runs(fresh), EXIT_DISABLED,
         ("::error::TIMER OFF", "reports this workflow as deleted"),
         "a workflow the API reports as deleted"),
        ({"path": "x", "state": "paused_for_vacation"}, _runs(fresh), EXIT_NOT_MEASURED,
         ("NOT MEASURED", "does not know", "is not an 'active'"),
         "a state string this script has never heard of"),
        ({"path": "x"}, _runs(fresh), EXIT_NOT_MEASURED,
         ("NOT MEASURED", "has no 'state' field"),
         "a workflow payload without a state"),
        ('"a string, which is valid JSON"', _runs(fresh), EXIT_NOT_MEASURED,
         ("NOT MEASURED", "is not a JSON object"),
         "a workflow payload that is not an object"),
        (active, _runs(fresh, event="push"), EXIT_NOT_MEASURED,
         ("NOT MEASURED", "the event filter did not hold", "says nothing about the timer"),
         "a runs payload whose newest run is not a scheduled one"),
        (active, {"total_count": 3, "workflow_runs": []}, EXIT_NOT_MEASURED,
         ("NOT MEASURED", "no run was listed"),
         "a count without the run it counts"),
        (active, _runs("Monday-ish"), EXIT_NOT_MEASURED,
         ("NOT MEASURED", "is not an RFC 3339 UTC timestamp"),
         "a timestamp that is not one"),
        (active, _runs("2026-11-20T00:00:00Z"), EXIT_NOT_MEASURED,
         ("NOT MEASURED", "in the future", "clocks disagree"),
         "a run that started in the future"),
    ):
        workflow_path, runs_path = _write(workflow), _write(runs)
        quiet, sys.stdout = sys.stdout, io.StringIO()
        _SELF_TEST_RUNNING = True
        try:
            actual = main(["schedule-liveness.py", workflow_path, runs_path, now])
        finally:
            _SELF_TEST_RUNNING = False
            spoken, sys.stdout = sys.stdout.getvalue(), quiet
            os.unlink(workflow_path)
            os.unlink(runs_path)
        if actual != wanted:
            failures.append("%s: expected exit %d, got %d" % (case, wanted, actual))
        for phrase in says:
            if phrase not in spoken:
                failures.append("%s: the output never says %r. It said: %s"
                                % (case, phrase, spoken.strip()))

    # The file-level failures, which the cases above cannot reach: they hand main() paths that
    # resolve, so the loader's own guards need their own cases. Each pins the cause it must name,
    # because "could not read the API response" with no reason sends a reader to the wrong place.
    absent = _write({})
    os.unlink(absent)
    for path, phrase, case in (
        (absent, "does not exist", "a payload file that is not there"),
        (_write(""), "is empty", "a zero-byte payload"),
        (_write('{"state": "act'), "not readable JSON", "a truncated payload"),
    ):
        quiet, sys.stdout = sys.stdout, io.StringIO()
        _SELF_TEST_RUNNING = True
        try:
            actual = main(["schedule-liveness.py", path, path, now])
        finally:
            _SELF_TEST_RUNNING = False
            spoken, sys.stdout = sys.stdout.getvalue(), quiet
            if os.path.exists(path):
                os.unlink(path)
        if actual != EXIT_NOT_MEASURED:
            failures.append("%s: expected exit %d, got %d" % (case, EXIT_NOT_MEASURED, actual))
        if phrase not in spoken:
            failures.append("%s: the output never says %r. It said: %s"
                            % (case, phrase, spoken.strip()))

    if failures:
        print("::error::The schedule-liveness self-test failed, so no verdict was formed:")
        for failure in failures:
            print("   - " + failure)
        return False
    return True


def main(argv):
    # This branch is the one line the self-test cannot reach, by construction: the flag that lets
    # the self-test call main() is exactly what skips it. Named here so it reads as a known
    # exception rather than an untested path nobody noticed.
    if not _SELF_TEST_RUNNING and not self_test():
        return EXIT_NOT_MEASURED

    if len(argv) not in (3, 4):
        print("usage: schedule-liveness.py <workflow.json> <runs.json> [now-ISO8601]")
        return EXIT_NOT_MEASURED

    max_age_days = DEFAULT_MAX_AGE_DAYS
    raw_window = os.environ.get("MAX_AGE_DAYS", "").strip()
    if raw_window:
        try:
            max_age_days = int(raw_window)
        except ValueError:
            print("::warning::NOT MEASURED: MAX_AGE_DAYS is %r, not a whole number of days. The "
                  "window is not guessed -- nothing was concluded." % raw_window)
            return EXIT_NOT_MEASURED

    try:
        if len(argv) == 4:
            now = _parse_timestamp(argv[3])
        else:
            now = datetime.datetime.now(datetime.timezone.utc)
        code, lines = verdict(
            _read_json(argv[1], "the workflow payload"),
            _read_json(argv[2], "the runs payload"),
            now,
            max_age_days,
        )
    except NotMeasured as reason:
        print("::warning::NOT MEASURED: %s" % reason)
        print("   This is not 'the timer is alive' and not a finding. Nothing was concluded about "
              "the weekly scan.")
        return EXIT_NOT_MEASURED

    for line in lines:
        print(line)
    return code


if __name__ == "__main__":
    sys.exit(main(sys.argv))
