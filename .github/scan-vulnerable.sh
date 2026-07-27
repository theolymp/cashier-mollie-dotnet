#!/usr/bin/env bash
#
# Fails the build when a dependency has a known advisory.
#
# `dotnet list package --vulnerable` exits 0 whether or not it finds anything -- measured, both
# cases return 0 -- so the exit code carries no signal and the output has to be read. That makes
# the third state the dangerous one: if the command fails, prints nothing, or changes its wording,
# a check that only greps for findings sees no findings and reports success. This script therefore
# requires a *recognised* verdict and treats anything else as a failure.
set -uo pipefail

output=$(dotnet list package --vulnerable --include-transitive 2>&1)
status=$?
echo "$output"

if [ "$status" -ne 0 ]; then
    echo "::error::dotnet list package failed (exit $status) -- no verdict, treating as failure"
    exit 1
fi

if grep -q 'has the following vulnerable packages' <<<"$output"; then
    echo "::error::Vulnerable dependencies found -- see the table above"
    exit 1
fi

if grep -q 'no vulnerable packages' <<<"$output"; then
    echo "No known advisories."
    exit 0
fi

echo "::error::Unrecognised output from dotnet list package -- cannot conclude the scan ran"
exit 1
