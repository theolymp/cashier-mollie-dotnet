#!/usr/bin/env bash
#
# Generates the CycloneDX SBOM and forms a license verdict over it.
#
# Two BOMs, because the two questions are different:
#
#   sbom/bom-solution.json  every component the repository restores. This is the artifact: an SBOM
#                           is asked for as a complete inventory, so completeness wins here.
#   sbom/bom-shipped.json   the library project only. This is what the license verdict gates on,
#                           for the same reason scan-vulnerable.sh scopes to it: a copyleft test
#                           framework is not redistributed by this package, and a verdict that is
#                           permanently red for something we do not ship stops meaning anything.
#
# Both are uploaded. The split is a *property* ("what ships") rather than a list of exceptions,
# which would go stale with the next package.
#
# Requires a prior `dotnet restore`: the generator reads obj/project.assets.json and does not
# create it. Without one it writes no BOM, and the guards below then report "not measured" --
# which is the correct outcome, not a clean one.
set -uo pipefail

readonly SHIPPED_PROJECT="src/CashierMollie/CashierMollie.csproj"
readonly SOLUTION="CashierMollie.sln"
readonly OUT_DIR="sbom"

# The generator version is pinned, and it is installed into a private tool path rather than
# globally. Both halves are deliberate:
#
#   Pinned, because an unpinned tool is a floating dependency, and this repository has already
#   been turned red by one: an unpinned SDK plus `AnalysisLevel: latest-recommended` imported a
#   new analyzer rule on 2026-07-27 and TreatWarningsAsErrors made it a build failure. A BOM
#   generator that changes its output shape between runs is the same class of problem, except the
#   thing that changes is evidence rather than a compile.
#
#   A private tool path, because `dotnet tool install --global` fails when the tool is already
#   installed at a different version -- on a developer machine that means the pinned version is
#   the one thing that does NOT run. With --tool-path the pin is what executes, every time, and
#   nothing is left behind on the machine.
readonly CYCLONEDX_VERSION="6.0.0"

fail_not_measured() {
    echo "::error::NOT MEASURED: $1"
    echo "This is not a license verdict and not '0 findings'. Nothing was concluded."
    exit 1
}

# The classifier parses JSON, which is why it is Python rather than grep: a verdict has to be tied
# to the component it belongs to, and grep cannot carry that association. The interpreter is
# therefore a dependency, and a missing one must not read as a clean run -- a sibling repository's
# version of this check used python3 inside the dotnet SDK image, where it is absent, and died with
# rc=127. This job runs on the GitHub-hosted Ubuntu image, which ships python3; the guard is here
# for the day it runs somewhere else.
command -v python3 >/dev/null 2>&1 \
    || fail_not_measured "python3 is not on PATH, so the license classifier cannot run"

TOOL_DIR="$(mktemp -d)" || fail_not_measured "could not create a directory for the BOM generator"
trap 'rm -rf "$TOOL_DIR"' EXIT

echo "--- Installing CycloneDX $CYCLONEDX_VERSION"
dotnet tool install --tool-path "$TOOL_DIR" CycloneDX --version "$CYCLONEDX_VERSION" \
    || fail_not_measured "CycloneDX $CYCLONEDX_VERSION could not be installed"

readonly GENERATOR="$TOOL_DIR/dotnet-CycloneDX"
[ -x "$GENERATOR" ] || fail_not_measured "$GENERATOR is missing after a successful install"

mkdir -p "$OUT_DIR"

# `--json` exists but is deprecated in favour of `--output-format`; the deprecated spelling prints
# a warning and would be the first thing to break in a major version of the generator.
generate() {
    local target="$1" filename="$2" label="$3"
    echo "--- BOM for $label"
    if ! "$GENERATOR" "$target" --output "$OUT_DIR" --filename "$filename" --output-format Json; then
        fail_not_measured "the generator failed for $label"
    fi
    [ -s "$OUT_DIR/$filename" ] \
        || fail_not_measured "$OUT_DIR/$filename is missing or empty after generating $label"
}

# The verdict scopes to ONE packable project, named above. That is correct today and silently
# wrong the moment a second one appears: the gate would judge this project's dependencies and say
# nothing at all about the other project's, which reads as an all-clear over half of what ships.
# A peer session hit exactly this through a different door -- their scope flag accepted repeats but
# `next(...)` took the first and dropped the rest, giving one repo a clean verdict over half its
# shipped set. Their summary of it is the one to keep: a tool that answers correctly and answers a
# different question.
#
# So the assumption is asserted rather than documented. IsPackable is the authoritative property
# (measured: true for the library, false for the test project), so adding a packable project makes
# this stop loudly instead of halving the scope quietly.
packable=""
while IFS= read -r project; do
    [ -n "$project" ] || continue
    value="$(dotnet msbuild "$project" -getProperty:IsPackable -nologo 2>/dev/null | tr -d '\r\n')"
    [ "$value" = "true" ] && packable="$packable$project"$'\n'
done <<EOF
$(find . -name '*.csproj' | sed 's|^\./||' | sort)
EOF
expected="$SHIPPED_PROJECT"$'\n'
if [ "$packable" != "$expected" ]; then
    fail_not_measured "the packable projects are not the one this gate scopes to.
Expected exactly:
$expected
Measured:
${packable:-<none>}
The license verdict covers \$SHIPPED_PROJECT alone, so any other packable project would ship
dependencies this run never classified. Add it to this script -- one BOM per packable project,
each non-empty in its own right -- rather than widening SHIPPED_PROJECT."
fi

generate "$SOLUTION" "bom-solution.json" "the whole solution"
generate "$SHIPPED_PROJECT" "bom-shipped.json" "the shipped library"

echo "--- License verdict"
python3 -I .github/license-classify.py "$OUT_DIR/bom-shipped.json" "$OUT_DIR/bom-solution.json"
verdict=$?

# THREE TIERS, and the classifier's exit code IS the tier. Project licence policy, 2026-11-07:
#
#   0  free          permissive, plus MPL-2.0        -> green
#   1  case-by-case  LGPL, EPL, CDDL, MS-PL, MPL-1.x, UNKNOWN
#                    -> reported, does NOT fail. The policy asks for a written decision, not a
#                       stop, and the run keeps naming them until there is one.
#   2  NOT MEASURED  no usable BOM, or a failed self-test -> HARD FAIL
#   3  STOP          GPL, AGPL, SSPL, BUSL, PROPRIETARY   -> HARD FAIL
#
# THE GAP BETWEEN 2 AND 3 IS LOAD-BEARING. Reusing 2 for a policy stop would make "red because the
# licence is refused" indistinguishable from "red because the gate knows nothing" -- opposite
# problems with opposite fixes. Case-by-case keeps 1 so that a caller which has not been updated
# meets an unknown 3 on a stop licence and fails hard, instead of quietly reading it as yellow.
#
# This replaces an advisory verdict whose flip condition was waiting on a decision. Two tiers were
# the defect: with permissive-or-finding, an LGPL dependency that policy wants documented produced
# the same yellow as an AGPL one that policy wants stopped, and a signal that cannot tell those
# apart stops being read. Nothing here is gated on a date.
#
# Scope is unchanged and is a property, not an exception list: only SHIPPED components decide the
# code. A test-only STOP licence is reported loudly and does not fail the build, because this
# package does not redistribute it.
case "$verdict" in
    0)
        echo ">> Free tier: every shipped component carries a licence the policy accepts."
        exit 0
        ;;
    1)
        echo "::warning::Case-by-case licences above need a documented decision. Reported, not"
        echo "   blocking -- the policy asks for them to be written down, and every run names"
        echo "   them until they are."
        exit 0
        ;;
    2)
        echo "::error::NOT MEASURED -- no verdict was formed. This is not a licence finding and"
        echo "   not an all-clear; see the output above for which guard fired."
        exit 1
        ;;
    3)
        echo "::error::STOP -- a shipped component carries a licence the policy refuses."
        echo "   Redistribution is refused or conditional, which a release cannot carry."
        exit 1
        ;;
    *)
        echo "::error::The classifier returned $verdict, which is not a tier this script knows."
        echo "   Treated as a failure on purpose: an unrecognised verdict is not a pass."
        exit 1
        ;;
esac
