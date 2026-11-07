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

generate "$SOLUTION" "bom-solution.json" "the whole solution"
generate "$SHIPPED_PROJECT" "bom-shipped.json" "the shipped library"

echo "--- License verdict"
python3 -I .github/license-classify.py "$OUT_DIR/bom-shipped.json" "$OUT_DIR/bom-solution.json"
verdict=$?

# REPORTING ONLY, and the condition for changing that is a STATE, not a date.
#
#   exit 2 -> not measured, or the classifier failed its own self-test. HARD failure: a gate that
#             cannot tell you it knows nothing is worse than no gate.
#   exit 1 -> measured, and there is a finding. Reported, does not fail the build.
#   exit 0 -> measured, everything shipped is permissive.
#
# Flip MODE to gate -- replace the `exit 0` in the `1)` branch with `exit 1` -- once every
# component in the shipped BOM classifies as PERMISSIVE, which is exactly the `0)` branch below.
#
# That wording is deliberate and it is a correction. This comment used to say "carries a license
# this classifier can name", which was the same set as permissive when it was written and stopped
# being so the moment the PROPRIETARY class was added: a vendor EULA IS named, so the old sentence
# would have read as satisfied with a proprietary dependency sitting in the shipped set -- a
# condition that announces itself as met while the thing it guards against is present. The
# mechanism was always right; only the sentence describing it was wrong, which is the worse half
# to get wrong, because the sentence is what a reader acts on.
#
# Today one shipped component is not permissive: Mollie.Api 4.18.0 declares the legacy NuGet
# licenseUrl instead of an SPDX expression, so it classifies as UNKNOWN (measured 2026-11-07).
#
# ⚠️ AND THE FLIP CARRIES AN UNDECIDED POLICY CHOICE, which is not this script's to make.
# "Permissive" excludes weak copyleft -- MPL, LGPL, EPL, CDDL, MS-PL -- so flipping this would
# also decide that an MPL-licensed *dependency* blocks a release of an MIT-licensed library. For a
# library that only consumes such a package without modifying it, that is defensible but it is a
# decision, and nobody has made it. The classifier has two outcomes, permissive or a finding; a
# policy would plausibly want three, with weak copyleft as a documented case rather than a stop.
#
# Measured 2026-11-07, which is why this is a note and not a bug: 58 components, 8 of them
# shipped, and ZERO in any weak-copyleft class (known-positive: the classifier does recognise
# MPL-2.0, LGPL-2.1-only and EPL-2.0 -- the zero is a real negative, not a blind spot). So the
# choice costs nothing today, and that is exactly how it would reach the person cutting a tag:
# unnoticed until the first MPL dependency arrives, and then decided under time pressure by
# whoever the red build belongs to. Answer it before the flip, not at the moment it first bites.
# Not a date, which would pass unnoticed -- the 90-day retention comment in ci.yml records a break
# date in this fleet being missed by three weeks -- and not a judgement call, because the run
# itself checks the condition and says so on the line below.
case "$verdict" in
    0)
        echo ">> THE CONDITION FOR BLOCKING IS MET ON THIS RUN: every shipped component is"
        echo "   permissive, so this run would also have been green with the verdict blocking."
        echo "   Flipping it is now a POLICY step and not a measurement -- it would also settle"
        echo "   whether weak copyleft in a shipped dependency stops a release, which nobody has"
        echo "   decided and which costs nothing today only because there is none. See the"
        echo "   comment above this case statement before changing the line below it."
        exit 0
        ;;
    1)
        echo "::warning::License findings above are advisory today and do not fail the build."
        echo "   They will keep being reported on every run; see the comment in this script for"
        echo "   the condition under which this becomes blocking."
        exit 0
        ;;
    *)
        echo "::error::The license check reached no verdict (classifier exit $verdict)."
        exit 1
        ;;
esac
