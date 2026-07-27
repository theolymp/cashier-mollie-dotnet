#!/usr/bin/env bash
#
# Fails the build when a *shipped* dependency has a known advisory.
#
# Two things this guards against, both measured rather than assumed:
#
#   1. `dotnet list package --vulnerable` exits 0 whether or not it finds anything, so the exit
#      code carries no signal and the output has to be read. That makes the third state the
#      dangerous one: a command that failed, printed nothing, or changed its wording produces no
#      findings, and a check that only greps for findings calls that success. This script requires
#      a *recognised* verdict and treats anything else as failure.
#
#   2. Scope. Scanning the whole solution mixes test-only packages into the verdict, and an
#      advisory for a test framework would then block a release of a package that never ships it.
#      The library project gates; the test project is reported but does not fail the build. Test
#      dependencies still run on CI machines, so they are not silenced -- only de-escalated.
#
#      MEASURED CAVEAT, so nobody credits this with more than it does: in *this* repo the scope
#      split is currently overshadowed. `TreatWarningsAsErrors` turns the NuGet audit warning
#      NU1903 into a restore error, so a vulnerable package fails the build before this script can
#      form a verdict -- for the test project exactly as hard as for the library. Verified both
#      ways: without TreatWarningsAsErrors the restore succeeds, the scan reports the finding and
#      the split works as intended; with it, the restore aborts and this script reports "no
#      verdict", which is also a failure, just a differently worded one.
#
#      The split is kept because it is correct and becomes visible the moment the audit does not
#      fire first -- an advisory published after the last restore, or a project that does not
#      treat warnings as errors. It is not, today, what protects this repository.
#
# Dependencies are deliberately bash + grep only. A sibling repo's version of this check used
# python3, which is absent from the dotnet SDK image and failed there with rc=127.
set -uo pipefail

readonly SHIPPED_PROJECT="src/CashierMollie/CashierMollie.csproj"
readonly TEST_PROJECT="tests/CashierMollie.Tests/CashierMollie.Tests.csproj"

readonly FOUND_MARKER='has the following vulnerable packages'
readonly CLEAN_MARKER='no vulnerable packages'

# Classify one scan output: 0 = clean, 1 = vulnerabilities, 2 = unusable (do not conclude).
classify() {
    local text="$1"
    if grep -q "$FOUND_MARKER" <<<"$text"; then return 1; fi
    if grep -q "$CLEAN_MARKER" <<<"$text"; then return 0; fi
    return 2
}

# Proves the classifier still works, on every run, without importing a real vulnerability. A gate
# whose detection is never exercised is indistinguishable from one that cannot fire.
self_test() {
    local failures=0

    classify "Project \`x\` $FOUND_MARKER"$'\n   Top-level Package   Severity'
    [ $? -eq 1 ] || { echo "SELF-TEST: a findings report was not classified as vulnerable"; failures=1; }

    classify "The given project \`x\` has $CLEAN_MARKER given the current sources."
    [ $? -eq 0 ] || { echo "SELF-TEST: a clean report was not classified as clean"; failures=1; }

    classify "error NU1301: Unable to load the service index for source"
    [ $? -eq 2 ] || { echo "SELF-TEST: an error was not classified as unusable"; failures=1; }

    classify ""
    [ $? -eq 2 ] || { echo "SELF-TEST: empty output was not classified as unusable"; failures=1; }

    if [ "$failures" -ne 0 ]; then
        echo "::error::Vulnerability classifier self-test failed -- the gate cannot be trusted"
        exit 1
    fi
    echo "Self-test passed: findings, clean and unusable outputs are told apart."
}

scan() {
    local project="$1" label="$2" gating="$3" output status
    output=$(dotnet list "$project" package --vulnerable --include-transitive 2>&1)
    status=$?
    echo "--- $label"
    echo "$output"

    if [ "$status" -ne 0 ]; then
        echo "::error::dotnet list package failed for $label (exit $status) -- no verdict"
        return 1
    fi

    classify "$output"
    case $? in
        0) echo "$label: no known advisories." ; return 0 ;;
        1)
            if [ "$gating" = "gate" ]; then
                echo "::error::$label has vulnerable dependencies -- see the table above"
                return 1
            fi
            echo "::warning::$label has vulnerable dependencies. Not shipped, so this does not fail the build -- fix it anyway, it runs on CI machines."
            return 0
            ;;
        *) echo "::error::Unrecognised output for $label -- cannot conclude the scan ran" ; return 1 ;;
    esac
}

self_test

rc=0
scan "$SHIPPED_PROJECT" "shipped library" gate   || rc=1
scan "$TEST_PROJECT"    "test project"    report || rc=1
exit "$rc"
