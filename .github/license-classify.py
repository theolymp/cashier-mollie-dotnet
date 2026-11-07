#!/usr/bin/env python3
"""Classifies the third-party licenses in a CycloneDX BOM.

The exit codes are the point of this script, and they are three-valued deliberately:

    0  measured, and every shipped component carries a license recognised as permissive
    1  measured, and there is something to look at -- copyleft, or a license we cannot name
    2  NOT MEASURED -- no usable BOM, or the classifier failed its own self-test

The third state is the one that costs money when it is missing. A check that only looks for
copyleft reads "I parsed nothing" as a pass. And a component that *has* a `licenses` array is not
thereby a component with a known license: measured in this repository on 2026-11-07, Mollie.Api
4.18.0 carries

    {"license": {"name": "Unknown - See URL", "url": "https://aka.ms/deprecateLicenseUrl"}}

-- present, populated, and saying nothing, because the package declares the legacy NuGet
`licenseUrl` instead of an SPDX expression. That is not "no license"; it is a license this tool
cannot name, which is a different fact and needs a human. It is reported as UNKNOWN and never
folded into permissive.

Scope split, same reasoning as scan-vulnerable.sh: the shipped library decides the exit code, the
test-only components are listed and cannot. A copyleft test framework is not redistributed by the
package, and a verdict that is permanently red for something we do not ship stops meaning anything
within days.

Ordering is semantics, not cosmetics. Within one component several licenses all apply, so the
*strongest* wins; inside an expression `A OR B` is a choice, so the *weakest* branch wins -- which
is why `BSD-3-Clause OR GPL-2.0-only` is permissive rather than copyleft. UNKNOWN sits between the
two groups: `MIT AND <unnameable>` cannot be concluded, while `GPL-3.0 AND <unnameable>` already
is, and `GPL-2.0 OR <unnameable>` cannot be, because the branch we would pick is the one we cannot
read.
"""
import json
import os
import re
import sys

# Strongest copyleft first. Each entry: (class, pattern, why it matters to a redistributed package)
CLASSES = [
    ("AGPL", r"\bAGPL", "network copyleft -- bites on SaaS without any distribution at all"),
    ("SSPL", r"\bSSPL", "not OSI-approved, hostile to offering the software as a service"),
    ("BUSL", r"\b(BUSL|BSL-1\.1|Business Source)", "source-available, not open source; time-delayed grant"),
    ("GPL", r"\bGPL", "strong copyleft -- fully binding on a redistributed artifact"),
    ("LGPL", r"\bLGPL", "weak copyleft -- usually fine when linked dynamically, not when statically"),
    ("MPL", r"\bMPL", "file-level copyleft -- changes to MPL files stay open"),
    ("EPL", r"\bEPL", "Eclipse Public License -- weak copyleft"),
    ("CDDL", r"\bCDDL", "file-level copyleft, GPL-incompatible"),
    ("MS-PL", r"\bMS-PL", "mild file-level copyleft; use and linking are free"),
]

# `PostgreSQL` is in here on purpose: the PostgreSQL License is a BSD-style permissive license and
# Npgsql ships under it. A list that omitted it would report Npgsql as UNKNOWN -- which is the
# correct behaviour for a name the tool does not know, and exactly why the fix belongs here rather
# than in a special case at the call site.
#
# The trailing \b matters and is cheap to get wrong: without it `\bMIT` also matches "MITRE", so a
# package named after an organisation would be read as MIT-licensed.
PERMISSIVE = re.compile(
    r"\b(MIT|MIT-0|ISC|BSD|0BSD|Apache|Unlicense|CC0|Zlib|WTFPL|Python-2|BlueOak|PostgreSQL)\b",
    re.IGNORECASE,
)

UNKNOWN = "UNKNOWN"
PERMISSIVE_NAME = "permissive"

# Licenses this script cannot read out of the BOM, resolved by hand at the package itself.
#
# Keyed by (name, version) EXACTLY, and that is the whole design: a version bump drops out of the
# table and the component goes back to UNKNOWN. An exception list matching by name would cover
# versions nobody ever looked at, and silently, which is the usual way such a list stops being
# true. This one expires by itself. Every entry that applies is printed on each run, green or not,
# so it cannot sit here unread.
RESOLVED_BY_HAND = {
    ("xunit.abstractions", "2.0.3"): (
        "Apache-2.0",
        "read at the package itself: its nuspec and the license text it ships. Caveat worth "
        "keeping: the license URL the package declares points at a branch rather than a tag for "
        "this version, so the link does not establish it and the text does",
    ),
}

# Severity order: most severe first, least severe last. Rank is an index into this list, so "the
# strongest of these" is min() and "the weakest of these" is max() -- see the module docstring for
# why AND takes the strongest and OR the weakest.
ORDER = [name for name, _, _ in CLASSES] + [UNKNOWN, PERMISSIVE_NAME]
WHY = {name: why for name, _, why in CLASSES}
WHY[UNKNOWN] = "expression belongs to no class this script knows -- needs a human"
WHY[PERMISSIVE_NAME] = ""

BLOCKING = {"AGPL", "SSPL", "BUSL", "GPL"}


class NotMeasured(Exception):
    """Raised when no verdict can be formed. Never the same thing as a clean verdict."""


def _rank(name):
    return ORDER.index(name)


def _classify_atom(expression):
    """One license expression with no OR and no AND left in it."""
    for name, pattern, _ in CLASSES:
        if re.search(pattern, expression, re.IGNORECASE):
            return name
    if PERMISSIVE.search(expression):
        return PERMISSIVE_NAME
    return UNKNOWN


def classify(expression):
    """Classify one license expression, honouring OR as a choice and AND as a conjunction."""
    cleaned = expression.replace("(", " ").replace(")", " ").strip()
    if not cleaned:
        return UNKNOWN
    branches = re.split(r"\s+OR\s+", cleaned, flags=re.IGNORECASE)
    branch_ranks = []
    for branch in branches:
        atoms = [a for a in re.split(r"\s+AND\s+", branch, flags=re.IGNORECASE) if a.strip()]
        if not atoms:
            branch_ranks.append(_rank(UNKNOWN))
            continue
        # Everything in a branch applies at once, so the strongest atom decides the branch.
        branch_ranks.append(min(_rank(_classify_atom(atom)) for atom in atoms))
    # The branches are alternatives we may pick from, so the weakest branch decides.
    return ORDER[max(branch_ranks)]


def license_expressions(component):
    """Every license expression declared for one component, as written in the BOM."""
    found = []
    for entry in component.get("licenses") or []:
        if "expression" in entry:
            found.append(entry["expression"])
            continue
        license = entry.get("license") or {}
        value = license.get("id") or license.get("name")
        if value:
            found.append(value)
    return found


def component_verdict(component):
    """(class, expression-as-shown) for one component. No declaration at all is UNKNOWN."""
    expressions = license_expressions(component)
    if not expressions:
        verdict, shown = UNKNOWN, "<none declared>"
    else:
        # Several license entries on one component all apply, so the strongest decides.
        verdict, shown = min(
            ((classify(e), e) for e in expressions), key=lambda pair: _rank(pair[0]))

    # A hand resolution only ever replaces UNKNOWN. If the BOM names a license, the BOM wins: an
    # override able to overrule a declared expression would be a way to silence a real finding,
    # which is the opposite of what this table is for.
    resolved = RESOLVED_BY_HAND.get((component.get("name"), component.get("version")))
    if resolved is not None and verdict == UNKNOWN:
        license_name = resolved[0]
        return classify(license_name), "%s (resolved by hand, was %r)" % (license_name, shown)
    return verdict, shown


def load_components(path, label):
    """Read a BOM, or raise NotMeasured. Every failure here is 'no verdict', not 'clean'."""
    if not os.path.exists(path):
        raise NotMeasured("%s: %s does not exist" % (label, path))
    if os.path.getsize(path) == 0:
        raise NotMeasured("%s: %s is empty" % (label, path))
    try:
        with open(path, encoding="utf-8") as handle:
            document = json.load(handle)
    except (ValueError, OSError) as error:
        raise NotMeasured("%s: %s is not readable JSON (%s)" % (label, path, error))
    if not isinstance(document, dict):
        raise NotMeasured("%s: top level of %s is not an object" % (label, path))
    if document.get("bomFormat") != "CycloneDX":
        # Shape assertion, not pedantry: anything else means the generator changed or a different
        # file was handed over, and then the verdict below would be about the wrong document.
        raise NotMeasured(
            "%s: bomFormat is %r, not 'CycloneDX'" % (label, document.get("bomFormat")))
    components = document.get("components")
    if not isinstance(components, list):
        raise NotMeasured("%s: no 'components' array" % label)
    if not components:
        # A BOM over zero components is indistinguishable from a scan that never happened, and it
        # would otherwise print "no copyleft found" -- green on the empty set.
        raise NotMeasured("%s: 0 components in the BOM -- nothing was scanned" % label)
    return components


def _key(component):
    return "%s@%s" % (component.get("name"), component.get("version"))


def report(shipped_path, solution_path):
    shipped = load_components(shipped_path, "shipped BOM")
    solution = load_components(solution_path, "solution BOM")
    if len(solution) < len(shipped):
        # The solution is a superset of the shipped project by construction. If it is not, the two
        # BOMs do not describe what their names say and the scope split below is meaningless.
        raise NotMeasured(
            "solution BOM has %d components, fewer than the shipped BOM's %d -- the two files do "
            "not describe what their names claim" % (len(solution), len(shipped)))

    shipped_keys = {_key(c) for c in shipped}
    findings = {"shipped": [], "test-only": []}

    print("Third-party licenses, by component (shipped library gates, test-only reports):")
    for component in sorted(solution, key=lambda c: (c.get("name") or "").lower()):
        verdict, expression = component_verdict(component)
        scope = "shipped" if _key(component) in shipped_keys else "test-only"
        print("  [%-9s] %-9s %-55s %s" % (scope, verdict, _key(component), expression))
        if verdict != PERMISSIVE_NAME:
            findings[scope].append((_key(component), verdict, expression))

    print("")
    print("Measured: %d components in the solution, %d of them shipped in the package."
          % (len(solution), len(shipped_keys)))

    # Printed whether or not the run is green: a resolution nobody sees is a resolution nobody
    # rechecks, and this is the one part of the verdict resting on reading rather than parsing.
    present = {(c.get("name"), c.get("version")) for c in solution}
    for (name, version), (license_name, why) in sorted(RESOLVED_BY_HAND.items()):
        if (name, version) in present:
            print("Resolved by hand: %s@%s is %s -- %s" % (name, version, license_name, why))
        else:
            print("Note: the hand resolution for %s@%s matched nothing in this BOM. It is "
                  "version-bound, so a bump makes that component UNKNOWN again until someone "
                  "looks." % (name, version))

    for scope in ("shipped", "test-only"):
        for name, verdict, expression in findings[scope]:
            severity = "blocking copyleft" if verdict in BLOCKING else (
                "cannot be named" if verdict == UNKNOWN else "weak copyleft")
            print("::warning::%s (%s): %s -- %s, %s. %s"
                  % (name, scope, expression, verdict, severity, WHY[verdict]))

    if findings["shipped"]:
        print("%d of the shipped components need a look (see the warnings above)."
              % len(findings["shipped"]))
        return 1
    if findings["test-only"]:
        print("Shipped components are all permissive. %d test-only components need a look; they "
              "are not redistributed by this package, so they do not decide this verdict."
              % len(findings["test-only"]))
        return 0
    print("All %d components resolve to a permissive license." % len(solution))
    return 0


def self_test():
    """Known-positive AND known-negative, on every run.

    A classifier that is only asked to find what it is meant to find is blind to the failure that
    matters here: quietly recognising nothing and printing that as an all-clear.
    """
    failures = []

    def expect(expression, wanted):
        actual = classify(expression)
        if actual != wanted:
            failures.append("classify(%r) = %r, expected %r" % (expression, actual, wanted))

    # Known-positive: the classes must actually fire.
    expect("MIT", PERMISSIVE_NAME)
    expect("Apache-2.0", PERMISSIVE_NAME)
    expect("BSD-3-Clause", PERMISSIVE_NAME)
    expect("PostgreSQL", PERMISSIVE_NAME)
    expect("GPL-3.0-only", "GPL")
    expect("AGPL-3.0-or-later", "AGPL")
    expect("LGPL-2.1-only", "LGPL")
    expect("MPL-2.0", "MPL")
    expect("SSPL-1.0", "SSPL")
    expect("MS-PL", "MS-PL")

    # Known-negative: these must NOT come out permissive.
    expect("Unknown - See URL", UNKNOWN)
    expect("", UNKNOWN)
    expect("Totally-Made-Up-License-1.0", UNKNOWN)
    expect("MITRE-Something", UNKNOWN)          # trailing \b, or this reads as MIT
    expect("GPL-2.0 WITH MIT-exception", "GPL")  # ordering: copyleft is checked before permissive

    # Expression algebra.
    expect("BSD-3-Clause OR GPL-2.0-only", PERMISSIVE_NAME)   # a choice -- take the BSD branch
    expect("GPL-2.0-only OR LGPL-2.1-only", "LGPL")           # a choice between two copylefts
    expect("MIT AND GPL-3.0-only", "GPL")                     # both apply -- the strongest binds
    expect("MIT AND Totally-Made-Up-1.0", UNKNOWN)            # cannot conclude
    # Both orders, because order-dependence is how this went wrong in another implementation of the
    # same classifier: with no copyleft class matching, it fell through to the first result, and the
    # same package got two different verdicts depending on how the expression happened to be
    # written. The reassuring direction is the one people write first.
    expect("Totally-Made-Up-1.0 AND MIT", UNKNOWN)
    expect("GPL-3.0-only OR Totally-Made-Up-1.0", UNKNOWN)    # the branch we would pick is unread
    expect("(MIT OR Apache-2.0)", PERMISSIVE_NAME)

    # Component-level: a missing or empty licenses array is UNKNOWN, not absent from the report.
    for component, wanted in (
        ({"name": "x", "version": "1"}, UNKNOWN),
        ({"name": "x", "version": "1", "licenses": []}, UNKNOWN),
        ({"name": "x", "version": "1", "licenses": [{"license": {"url": "http://e"}}]}, UNKNOWN),
        ({"name": "x", "version": "1", "licenses": [{"license": {"id": "MIT"}}]}, PERMISSIVE_NAME),
        ({"name": "x", "version": "1", "licenses": [{"expression": "MIT OR GPL-2.0-only"}]},
         PERMISSIVE_NAME),
        # Two entries both apply: MIT does not excuse the GPL one.
        ({"name": "x", "version": "1", "licenses": [{"license": {"id": "MIT"}},
                                                    {"license": {"id": "GPL-3.0-only"}}]}, "GPL"),
        # A hand resolution applies at its exact version ...
        ({"name": "xunit.abstractions", "version": "2.0.3",
          "licenses": [{"license": {"name": "Unknown - See URL"}}]}, PERMISSIVE_NAME),
        # ... and at no other, which is what keeps the table from covering what nobody read.
        ({"name": "xunit.abstractions", "version": "2.0.4",
          "licenses": [{"license": {"name": "Unknown - See URL"}}]}, UNKNOWN),
        # And it never overrules a license the BOM actually names.
        ({"name": "xunit.abstractions", "version": "2.0.3",
          "licenses": [{"license": {"id": "GPL-3.0-only"}}]}, "GPL"),
    ):
        actual = component_verdict(component)[0]
        if actual != wanted:
            failures.append("component_verdict(%r) = %r, expected %r"
                            % (component, actual, wanted))

    # Loader: the measurement guards must fire, and must not fire on a valid BOM.
    valid = {"bomFormat": "CycloneDX", "components": [{"name": "x", "version": "1"}]}
    for document, should_fail in (
        (valid, False),
        ({"bomFormat": "CycloneDX", "components": []}, True),
        ({"bomFormat": "CycloneDX"}, True),
        ({"components": [{"name": "x"}]}, True),
        ([], True),
    ):
        path = os.path.join(os.environ.get("TMPDIR", "/tmp"), "cashier-license-selftest.json")
        with open(path, "w", encoding="utf-8") as handle:
            json.dump(document, handle)
        try:
            load_components(path, "self-test")
            failed = False
        except NotMeasured:
            failed = True
        os.unlink(path)
        if failed != should_fail:
            failures.append("load_components(%r) %s"
                            % (document, "rejected a valid BOM" if should_fail is False
                               else "accepted an unusable BOM"))

    if failures:
        print("::error::License classifier self-test failed -- this gate cannot be trusted:")
        for failure in failures:
            print("  %s" % failure)
        return False
    print("Self-test passed: permissive, copyleft and unnameable licenses are told apart, and an "
          "unusable BOM is told from an empty one.")
    return True


def main(argv):
    if not self_test():
        return 2
    if argv[1:] == ["--self-test"]:
        return 0
    if len(argv) != 3:
        print("usage: %s <shipped-bom.json> <solution-bom.json>" % argv[0])
        print("       %s --self-test" % argv[0])
        return 2
    try:
        return report(argv[1], argv[2])
    except NotMeasured as error:
        print("::error::NOT MEASURED: %s" % error)
        print("This is not a license verdict and it is not '0 copyleft findings'. Nothing was "
              "concluded.")
        return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv))
