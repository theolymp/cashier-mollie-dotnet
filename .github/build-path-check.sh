#!/usr/bin/env bash
#
# Packs the library twice -- once plain, once with PathMap -- and reports whether the absolute
# build path left the shipped assembly, and whether it left by being REWRITTEN.
#
# Background: `CashierMollie.dll` is published to nuget.org and ends up inside another
# repository's release image, carrying the build directory of OUR runner with it. Measured
# 2026-11-08, the carrier is a single string: the PDB path in the PE debug directory. Pantheon's
# CRA evidence rests on image digest plus release SBOM, and deterministic paths are what make two
# builds of the same source comparable.
#
# WHY A SECOND, UNMAPPED PACK: the mapped run alone cannot prove anything. A zero has two causes
# -- the path was rewritten, or the compiler never emitted one. The reference run is what tells
# those apart, and the decision refuses to form a verdict when the reference carries no path.
#
# WHAT THIS IS NOT: a byte comparison against a reference build. That is the discriminator a peer
# session uses and it does not hold here -- measured, two clean packs from the same root with the
# same flag differ in 72 of 199,168 bytes: PE stamp, MVID, and the PDB signature in the debug
# directory. IL and metadata are identical, only the identity GUIDs move. Byte equality would be
# a permanent false alarm in this repository, the same shape as a bolt on total matches, one axis
# over. The transferable measurement is the string count.
#
# The verdict lives in build-path-check.py, whose exit code IS the tier (0 proven, 2 not
# measured, 3 an absolute path survived).
set -uo pipefail

readonly PROJECT="src/CashierMollie/CashierMollie.csproj"
# The build root comes from the environment, never typed: on a runner it is the workspace, and a
# hardcoded path would map a directory the compiler never saw -- the flag would then be set,
# report success, and change nothing. A peer measured exactly that failure with a different flag.
readonly BUILD_ROOT="${GITHUB_WORKSPACE:-$PWD}"

# STRICT=1 turns "not measured" into a failure. Set on the publish job only: before an
# irreversible push, missing evidence is not an acceptable state, while on an ordinary push a
# transient pack failure must not be able to tear down the release path.
not_measured() {
    echo "::warning::NOT MEASURED: $1"
    echo "   This is not 'the build path is gone' and not a finding. Nothing was concluded about"
    echo "   the shipped assembly."
    [ "${STRICT:-0}" = "1" ] || exit 0
    echo "::error::STRICT: this ran before a publish, where missing evidence blocks the release."
    exit 1
}

command -v python3 >/dev/null 2>&1 \
    || not_measured "python3 is not on PATH, so the build-path verdict cannot be formed"

WORK_DIR="$(mktemp -d)" || not_measured "could not create a directory for the two packs"
trap 'rm -rf "$WORK_DIR"' EXIT

# obj/ and bin/ are removed before EACH pack. Without that the second invocation returns rc=0
# without recompiling, and then reports the first run's assembly as the second run's result.
pack() {
    local out="$1" label="$2"
    shift 2
    rm -rf src/CashierMollie/obj src/CashierMollie/bin
    if ! dotnet pack "$PROJECT" --configuration Release --output "$out" "$@" >"$WORK_DIR/$label.log" 2>&1; then
        tail -20 "$WORK_DIR/$label.log"
        not_measured "the $label pack failed, so there is nothing to compare"
    fi
}

echo "--- Reference pack (no PathMap)"
pack "$WORK_DIR/reference" "reference"

# MAPPED_PACKAGE points at an ALREADY BUILT package -- the one the publish job is about to
# push. Measuring that file rather than one this script packs itself is the difference between
# "a pack like the release would make" and "the release artifact": only the second can stop a bad
# push. Without the variable, the mapped pack is made here.
if [ -n "${MAPPED_PACKAGE:-}" ]; then
    echo "--- Mapped artifact taken as given: $MAPPED_PACKAGE"
    [ -f "$MAPPED_PACKAGE" ] \
        || not_measured "MAPPED_PACKAGE is set to '$MAPPED_PACKAGE', which is not a file"
    mkdir -p "$WORK_DIR/mapped"
    cp "$MAPPED_PACKAGE" "$WORK_DIR/mapped/" \
        || not_measured "could not read $MAPPED_PACKAGE"
else
    echo "--- Mapped pack (-p:PathMap=$BUILD_ROOT=.)"
    pack "$WORK_DIR/mapped" "mapped" "-p:PathMap=$BUILD_ROOT=."
fi

# Put the tree back the way the rest of the job needs it. This is not tidiness: the packs above
# delete obj/, which takes project.assets.json with it, and the next step in the workflow builds
# with --no-restore. Measured locally before this ran in CI: that build fails with NETSDK1004,
# and because the step after it tests with --no-build, the suite then reports green against the
# assembly from BEFORE this script ran. A measurement step that breaks the build it sits in
# front of, while the tests still pass, is worse than no measurement.
rm -rf src/CashierMollie/obj src/CashierMollie/bin
if ! dotnet restore "$PROJECT" >"$WORK_DIR/restore.log" 2>&1; then
    tail -20 "$WORK_DIR/restore.log"
    echo "::error::The packs above removed obj/ and the restore that puts it back failed, so the"
    echo "   following build would fail for a reason that has nothing to do with build paths."
    exit 1
fi

reference_pkg="$(ls "$WORK_DIR"/reference/*.nupkg 2>/dev/null | head -1)"
mapped_pkg="$(ls "$WORK_DIR"/mapped/*.nupkg 2>/dev/null | head -1)"
[ -n "$reference_pkg" ] && [ -n "$mapped_pkg" ] \
    || not_measured "one of the two packs produced no .nupkg, so the pair is incomplete"

python3 -I .github/build-path-check.py "$reference_pkg" "$mapped_pkg" "$BUILD_ROOT"
tier=$?

case "$tier" in
    0)
        exit 0
        ;;
    2)
        # Reported, not blocking on an ordinary run: an unmeasurable run means this evidence is
        # missing, not that the package is bad. The step is also marked continue-on-error in the
        # workflow, so a failure in the MEASUREMENT cannot break the build chain that publish
        # depends on -- a peer session had nine build jobs torn down by exactly that. Before a
        # publish, STRICT=1 reverses this.
        if [ "${STRICT:-0}" = "1" ]; then
            echo "::error::STRICT: no verdict was formed, and this ran before a publish."
            exit 1
        fi
        exit 0
        ;;
    3)
        echo "::error::The shipped assembly carries an absolute build path. The package would take"
        echo "   this runner's directory layout into every consumer's release image."
        exit 1
        ;;
    *)
        echo "::error::The build-path check returned $tier, which is not a tier this script knows."
        echo "   Treated as a failure on purpose: an unrecognised verdict is not a pass."
        exit 1
        ;;
esac
