#!/usr/bin/env bash
#
# Asks whether the weekly scan timer is still alive, and says so out loud in the run log.
#
# The gap this closes: a scheduled workflow re-scans the *published* package against an advisory
# database that moves while the code stands still -- and GitHub disables scheduled workflows in
# public repositories after 60 days without repository activity. Nothing fails when that happens.
# From outside, a dead timer and a clean scan look the same, so "no alert" was not evidence of a
# scan. It is now: the workflow's `state` field is read, and `disabled_inactivity` is one of its
# values.
#
# KNOWN LIMIT, and it is structural: this check runs *inside* the workflow it measures. GitHub's
# documentation does not say whether automatic disabling also stops the push and pull_request
# triggers. If it does, no run exists to report it, and the symptom to look for is different --
# a commit on main with no checks at all, rather than a red one. The check still earns its place
# for the cases it can reach (a manual disable, a fork, a timer that is active but stopped
# producing runs), and it cannot make the unreachable case worse.
#
# The verdict itself lives in schedule-liveness.py, which takes the two payloads as files: the
# fetch and its guards belong here, the decision belongs there with a self-test that drives every
# tier. Exit code of that script IS the tier (0 alive, 1 unclear, 2 not measured, 3 disabled).
set -uo pipefail

readonly REPO="${GITHUB_REPOSITORY:-theolymp/cashier-mollie-dotnet}"
readonly WORKFLOW_FILE="${WORKFLOW_FILE:-ci.yml}"
readonly API="https://api.github.com/repos/$REPO/actions/workflows/$WORKFLOW_FILE"

not_measured() {
    echo "::warning::NOT MEASURED: $1"
    echo "   This is not 'the timer is alive' and not a finding. Nothing was concluded about the"
    echo "   weekly scan -- see the line above for which step could not answer."
    exit 0
}

command -v python3 >/dev/null 2>&1 \
    || not_measured "python3 is not on PATH, so the liveness verdict cannot be formed"
command -v curl >/dev/null 2>&1 \
    || not_measured "curl is not on PATH, so the workflow state cannot be read"

WORK_DIR="$(mktemp -d)" || not_measured "could not create a directory for the API payloads"
trap 'rm -rf "$WORK_DIR"' EXIT

# Authenticated when a token is present, unauthenticated otherwise. Both work -- this repository
# is public -- but the unauthenticated limit is 60 requests an hour per IP and Actions runners
# share addresses, so an anonymous call here would be a flake waiting for a busy hour. The token
# needs `actions: read`, which the workflow grants explicitly.
fetch() {
    local url="$1" out="$2" label="$3" status rc
    local -a auth=()
    [ -n "${GITHUB_TOKEN:-}" ] && auth=(-H "Authorization: Bearer $GITHUB_TOKEN")
    status="$(curl -sS -o "$out" -w '%{http_code}' \
        -H "Accept: application/vnd.github+json" \
        -H "X-GitHub-Api-Version: 2022-11-28" \
        ${auth[@]+"${auth[@]}"} "$url")"
    rc=$?
    # Two different failures, kept apart on purpose: curl not running at all (rc != 0, no status)
    # and the API answering something other than 200. A 403 here is almost always the rate limit
    # or a missing `actions: read`, and a 404 means the workflow path in this script is stale --
    # which would otherwise read as "no timer", the very confusion this file exists to remove.
    #
    # rc is captured into a variable immediately: `[ $? -ne 0 ]` after a multi-line if/else block
    # happens to work here, and is exactly the kind of thing that stops working when someone adds
    # a line between the two.
    if [ "$rc" -ne 0 ] || [ -z "$status" ]; then
        not_measured "curl could not reach the API for $label (rc=$rc)"
    fi
    [ "$status" = "200" ] \
        || not_measured "the API answered HTTP $status for $label (403: rate limit or a token \
without actions:read; 404: $WORKFLOW_FILE is not the workflow's path any more)"
}

echo "--- Scan timer liveness for $REPO ($WORKFLOW_FILE)"
fetch "$API" "$WORK_DIR/workflow.json" "the workflow itself"
fetch "$API/runs?event=schedule&per_page=1" "$WORK_DIR/runs.json" "its scheduled runs"

python3 -I .github/schedule-liveness.py "$WORK_DIR/workflow.json" "$WORK_DIR/runs.json"
tier=$?

case "$tier" in
    0)
        exit 0
        ;;
    1|2)
        # Reported, not blocking. The age is inference (GitHub delays and drops scheduled runs
        # under load) and an unreadable API is a remote failure inside our run -- turning either
        # into a red main would make this detector the flake, for something that matters weekly
        # rather than per commit. The measured half is tier 3, and it does fail.
        exit 0
        ;;
    3)
        echo "::error::The weekly scan timer is OFF. The published package is not being"
        echo "   re-examined, and nothing else in this repository would have said so."
        exit 1
        ;;
    *)
        echo "::error::The liveness check returned $tier, which is not a tier this script knows."
        echo "   Treated as a failure on purpose: an unrecognised verdict is not a pass."
        exit 1
        ;;
esac
