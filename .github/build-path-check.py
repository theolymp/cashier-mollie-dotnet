#!/usr/bin/env python3
"""Decides whether the shipped assembly still carries the absolute build path.

Why: the published `CashierMollie.dll` ends up inside another repository's release image, and it
took GitHub Actions paths of OUR build along -- measured 2026-11-08, the carrier is exactly one
string, the PDB path in the PE debug directory:

    <build root>/src/CashierMollie/obj/Release/net10.0/CashierMollie.pdb

`-p:PathMap=$GITHUB_WORKSPACE=.` rewrites it to `./src/...`, which makes two builds of the same
source comparable on that axis. This file is the evidence that it happened, and it reports TWO
numbers rather than one, because a zero alone has a second explanation:

    absolute == 0   the path is gone
    rewritten >= 1  ...gone because it was REWRITTEN, not because the compiler stopped emitting
                    a path at all. Without the second number, removing the debug directory
                    entirely would read as a success.

Both numbers come from the same pair of runs, and a reference run without PathMap must show the
path in the first place -- otherwise nothing was demonstrated and the verdict is NOT MEASURED
rather than a pass.

Exit code IS the tier, as in license-classify.py and schedule-liveness.py:

  0  PROVEN        reference carried the path, mapped run carries none, rewrite visible
  2  NOT MEASURED  empty search set, reference run without a build path, unreadable package
  3  FOUND         an absolute build path survived into the shipped assembly

Deliberately NOT reporting a tier for "the flag is set": a peer session measured
`-p:ContinuousIntegrationBuild=true` as rc=0, property confirmed set by `-getProperty`, and the
assembly byte-identical -- the flag did nothing and looked like it had. A property is not an
effect, so nothing here reads one.

Counting is UTF-8 and UTF-16LE separately. On this package utf16 is 0 and ascii is 1, which is
this repository's base set and not a constant: a sibling repo measured 2 UTF-16 copies that must
stay 2, because copies that fell along with the rewrite would have belonged to the compiler.
"""
import io
import os
import re
import sys
import zipfile

EXIT_PROVEN = 0
EXIT_NOT_MEASURED = 2
EXIT_FOUND = 3

# The one assembly this package ships. Asserted rather than discovered, and this is the local
# shape of a peer's rule: they classify each assembly as own-or-foreign against the NuGet cache,
# with a known case per side. Here there is no foreign side to name -- dependencies are package
# references and are not redistributed -- so `foreign == 0` is the shape of the artifact, not a
# broken classifier. What can still go wrong is the search set silently becoming empty or
# growing, so that is what gets the bolt.
SHIPPED_ASSEMBLY = "lib/net10.0/CashierMollie.dll"

_SELF_TEST_RUNNING = False


class NotMeasured(Exception):
    """Raised when the inputs cannot answer the question. Never means 'no path'."""


def _occurrences(data, needle):
    """UTF-8 and UTF-16LE counts, kept apart."""
    utf8 = len(re.findall(re.escape(needle.encode("utf-8")), data))
    utf16 = len(re.findall(re.escape(needle.encode("utf-16-le")), data))
    return utf8, utf16


def shipped_assembly(package_path):
    if not os.path.exists(package_path):
        raise NotMeasured("%s does not exist, so nothing was packed to measure" % package_path)
    try:
        archive = zipfile.ZipFile(package_path)
    except (zipfile.BadZipFile, OSError) as error:
        raise NotMeasured("%s is not a readable package: %s" % (package_path, error))

    assemblies = [name for name in archive.namelist() if name.endswith(".dll")]
    # An empty search set satisfies "no absolute paths found" for any expectation.
    if not assemblies:
        raise NotMeasured("%s ships no assembly at all, so the search set is empty" % package_path)
    if assemblies != [SHIPPED_ASSEMBLY]:
        raise NotMeasured(
            "%s ships %r, expected exactly [%r]. The search set is the claim: an extra assembly "
            "would be judged by nothing, a renamed one by a check looking elsewhere."
            % (package_path, assemblies, SHIPPED_ASSEMBLY))
    return archive.read(assemblies[0])


def verdict(reference, mapped, build_root):
    """Returns (exit_code, lines). Raises NotMeasured when the pair cannot answer."""
    if not build_root or not build_root.startswith("/"):
        raise NotMeasured("the build root %r is not an absolute path" % build_root)

    ref_utf8, ref_utf16 = _occurrences(reference, build_root)
    map_utf8, map_utf16 = _occurrences(mapped, build_root)
    rewritten = len(re.findall(rb"\./src/CashierMollie/obj/", mapped))

    base = ("search set %s; reference run: %d UTF-8 + %d UTF-16LE; mapped run: %d + %d; "
            "rewritten relative paths in the mapped run: %d"
            % (SHIPPED_ASSEMBLY, ref_utf8, ref_utf16, map_utf8, map_utf16, rewritten))

    # Abort condition, not a pass: if the reference run carries no build path, the mapped run's
    # zero says nothing about PathMap -- it says the compiler was not emitting one anyway.
    if ref_utf8 + ref_utf16 == 0:
        raise NotMeasured(
            "the reference run (no PathMap) carries no absolute build path either, so a zero in "
            "the mapped run demonstrates nothing. Measured: " + base)

    lines = []
    if map_utf8 + map_utf16 > 0:
        lines.append("::error::An absolute build path survived into the shipped assembly.")
        lines.append("   %s" % base)
        lines.append("   The published package would carry this repository's build directory into "
                     "every consumer's image. Check that the Pack step still passes "
                     "-p:PathMap=$GITHUB_WORKSPACE=. and that the root it maps is the one the "
                     "compiler actually sees.")
        return EXIT_FOUND, lines

    # Second abort condition: zero absolute AND zero rewritten means the path vanished rather
    # than moved, which is a different change with a different cause -- and would otherwise be
    # indistinguishable from the success case.
    if rewritten == 0:
        raise NotMeasured(
            "no absolute build path remains, but no rewritten path is present either -- the path "
            "was removed rather than mapped, and this check cannot tell a fixed build from a "
            "debug directory that stopped being emitted. Measured: " + base)

    lines.append(">> Build path rewritten, not merely absent.")
    lines.append("   %s" % base)
    lines.append("   Two numbers on purpose: the zero says the absolute path is gone, the "
                 "rewrite says it is gone because it was mapped.")
    return EXIT_PROVEN, lines


def _package(entries):
    """A .nupkg-shaped zip built in memory, for the self-test."""
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, "w") as archive:
        for name, payload in entries.items():
            archive.writestr(name, payload)
    return buffer.getvalue()


def self_test():
    """Drives every tier through the decision, asserting the tier AND what the output names."""
    root = "/home/runner/work/cashier-mollie-dotnet/cashier-mollie-dotnet"
    carrier = (root + "/src/CashierMollie/obj/Release/net10.0/CashierMollie.pdb").encode()
    rewritten = b"./src/CashierMollie/obj/Release/net10.0/CashierMollie.pdb"
    failures = []

    for reference, mapped, wanted, says, case in (
        (carrier, rewritten, EXIT_PROVEN,
         ("Build path rewritten, not merely absent", "reference run: 1 UTF-8 + 0 UTF-16LE",
          "mapped run: 0 + 0", "rewritten relative paths in the mapped run: 1"),
         "the path rewritten, which is the whole point"),
        (carrier, carrier, EXIT_FOUND,
         ("::error::An absolute build path survived", "mapped run: 1 + 0", "PathMap"),
         "the flag missing or mapping the wrong root"),
        (carrier, b"no path of any kind here", EXIT_NOT_MEASURED,
         ("NOT MEASURED", "removed rather than mapped", "stopped being emitted"),
         "the path gone without a rewrite, which is not the same success"),
        (b"a reference run with no build path", rewritten, EXIT_NOT_MEASURED,
         ("NOT MEASURED", "carries no absolute build path either", "demonstrates nothing"),
         "a reference run that proves nothing"),
        # A reference run whose only hit is UTF-16LE is still a valid reference: the point of
        # counting both encodings is that either one proves the path was there to rewrite. This
        # case was written expecting NOT MEASURED and the code disagreed -- the code was right.
        (root.encode("utf-16-le"), rewritten, EXIT_PROVEN,
         ("Build path rewritten", "reference run: 0 UTF-8 + 1 UTF-16LE"),
         "a reference run carrying the path only as UTF-16LE"),
    ):
        quiet, sys.stdout = sys.stdout, io.StringIO()
        global _SELF_TEST_RUNNING
        _SELF_TEST_RUNNING = True
        try:
            actual = _decide(reference, mapped, root)
        finally:
            _SELF_TEST_RUNNING = False
            spoken, sys.stdout = sys.stdout.getvalue(), quiet
        if actual != wanted:
            failures.append("%s: expected exit %d, got %d" % (case, wanted, actual))
        for phrase in says:
            if phrase not in spoken:
                failures.append("%s: the output never says %r. It said: %s"
                                % (case, phrase, spoken.strip()))

    # UTF-16 is counted, not assumed absent: a carrier stored as UTF-16LE must be found, or a
    # repository whose copies live in that encoding would read as clean.
    wide = root.encode("utf-16-le") + b"/s\x00r\x00c\x00"
    quiet, sys.stdout = sys.stdout, io.StringIO()
    _SELF_TEST_RUNNING = True
    try:
        actual = _decide(carrier, wide, root)
    finally:
        _SELF_TEST_RUNNING = False
        spoken, sys.stdout = sys.stdout.getvalue(), quiet
    if actual != EXIT_FOUND or "0 + 1" not in spoken:
        failures.append("a UTF-16LE build path in the mapped run: expected exit %d and a UTF-16 "
                        "count of 1, got %d and %r" % (EXIT_FOUND, actual, spoken.strip()))

    # The search-set guards, which the cases above cannot reach: they bypass the package reader.
    for entries, phrase, case in (
        ({"README.md": "no assembly here"}, "search set is empty", "a package shipping no assembly"),
        ({SHIPPED_ASSEMBLY: "x", "lib/net10.0/Extra.dll": "y"}, "would be judged by nothing",
         "a package that grew a second assembly"),
        ({"lib/net10.0/Renamed.dll": "x"}, "by a check looking elsewhere",
         "a package whose assembly was renamed"),
    ):
        path = os.path.join(os.environ.get("TMPDIR", "/tmp"), "bpc-selftest.nupkg")
        with open(path, "wb") as handle:
            handle.write(_package(entries))
        try:
            shipped_assembly(path)
            failures.append("%s: no NotMeasured was raised" % case)
        except NotMeasured as reason:
            if phrase not in str(reason):
                failures.append("%s: the reason never says %r. It said: %s" % (case, phrase, reason))
        finally:
            os.unlink(path)

    if failures:
        print("::error::The build-path self-test failed, so no verdict was formed:")
        for failure in failures:
            print("   - " + failure)
        return False
    return True


def _decide(reference, mapped, build_root):
    try:
        code, lines = verdict(reference, mapped, build_root)
    except NotMeasured as reason:
        print("::warning::NOT MEASURED: %s" % reason)
        print("   This is not 'the build path is gone' and not a finding. Nothing was concluded.")
        return EXIT_NOT_MEASURED
    for line in lines:
        print(line)
    return code


def main(argv):
    # The one line the self-test cannot reach, by construction: the flag that lets it call the
    # decision is what skips this. Named so it reads as a known exception.
    if not _SELF_TEST_RUNNING and not self_test():
        return EXIT_NOT_MEASURED

    if len(argv) != 4:
        print("usage: build-path-check.py <reference.nupkg> <mapped.nupkg> <absolute-build-root>")
        return EXIT_NOT_MEASURED

    try:
        reference = shipped_assembly(argv[1])
        mapped = shipped_assembly(argv[2])
    except NotMeasured as reason:
        print("::warning::NOT MEASURED: %s" % reason)
        print("   This is not 'the build path is gone' and not a finding. Nothing was concluded.")
        return EXIT_NOT_MEASURED

    return _decide(reference, mapped, argv[3])


if __name__ == "__main__":
    sys.exit(main(sys.argv))
