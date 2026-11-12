#!/usr/bin/env bash
#
# Runs one filtered slice of the suite and fails unless it ACTUALLY RAN.
#
# `dotnet test` exits 0 for passed, for all-skipped and for zero matching tests alike -- measured
# 2026-10-04. The exit code therefore cannot tell "verified against this provider" from "measured
# nothing", and the provider-specific tests skip by design when their connection string is absent.
# So the passed count is read out of the output, the same way scan-vulnerable.sh reads findings
# rather than trusting a status.
#
# It also checks the status, which the inline version of this block did not: `dotnet test | tee`
# yields tee's status, and the GitHub default shell is `bash -e` without pipefail, so a FAILING
# provider test left that step green and was only caught later by the unfiltered run. Reading the
# count and ignoring the status answers "did it run" while quietly dropping "did it pass".
set -uo pipefail

filter="${1:?usage: test-must-run.sh <filter> <label>}"
label="${2:?usage: test-must-run.sh <filter> <label>}"

# One run PER TARGET FRAMEWORK since 2026-11-12. A single `dotnet test` over both frameworks runs
# them in parallel and prints two "Total tests / Passed: N" blocks that carry no framework name
# (measured), so reading the last count, as this did before, would let one framework skip the
# whole slice while the other's count made the step green. Running each framework on its own
# makes the count attributable. The list is read from the project file rather than written here,
# so adding a framework cannot leave this script checking a subset.
tfms=$(sed -n 's:.*<TargetFrameworks\{0,1\}>\([^<]*\)</TargetFrameworks\{0,1\}>.*:\1:p' \
    tests/CashierMollie.Tests/CashierMollie.Tests.csproj | head -1 | tr ';' ' ')
if [ -z "$tfms" ]; then
    echo "::error::Could not read the target frameworks of the test project, so there is nothing to hold the $label runs against."
    exit 1
fi

log="$(mktemp)" || { echo "::error::could not create a log file for $label"; exit 1; }
trap 'rm -f "$log"' EXIT

for tfm in $tfms; do
    dotnet test --no-build --configuration Release --framework "$tfm" --verbosity normal --filter "$filter" 2>&1 | tee "$log"
    status=${PIPESTATUS[0]}

    passed=$(sed -n 's/.*Passed: *\([0-9][0-9]*\).*/\1/p' "$log" | tail -1)
    # Measured: when every test in the slice skips, the summary prints "Skipped: 6" on its own line
    # and there is no "Passed: N" at all -- so the absent count, not a zero count, is what an
    # all-skipped run actually looks like. A run that matched no tests looks the same. Both are
    # kept as separate branches anyway: a format change that starts printing "Passed: 0" must not
    # fall through the gap between them.
    if [ -z "$passed" ]; then
        echo "::error::No 'Passed: N' line in the $tfm output for $label. That is what an all-skipped run looks like, and also what a run matching no tests looks like. Either way nothing was verified on $tfm."
        exit 1
    fi
    if [ "$passed" -lt 1 ]; then
        echo "::error::$label did not run on $tfm ($passed passed). These tests skip when their connection string is unset or the service container is unreachable; a skipped test and a passing one look the same in a summary line, which is why this is asserted."
        exit 1
    fi
    if [ "$status" -ne 0 ]; then
        echo "::error::$label ran on $tfm ($passed passed) but the run reported failures -- see the output above."
        exit 1
    fi
    echo "$label ran on $tfm: $passed passed."
done
