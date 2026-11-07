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
import io
import json
import os
import re
import sys

# Strongest copyleft first. Each entry: (class, pattern, why it matters to a redistributed package)
CLASSES = [
    # First because it is not a copyleft term at all but a different fact: the package is not open
    # source, and redistribution is a conditional grant rather than a right.
    #
    # Matched on the SPDX `LicenseRef-` prefix, which strictly means only "an identifier SPDX does
    # not define". So this class name asserts more than the prefix proves: a permissive in-house
    # licence expressed as a LicenseRef would be reported here too. That direction is deliberate --
    # over-report a custom licence, never wave one through -- and the actual terms live in the
    # evidence string of whatever named it, not in the class.
    ("PROPRIETARY", r"\bLicenseRef-",
     "not an SPDX licence: vendor terms, and redistribution is conditional. Read them before "
     "shipping anything that carries this"),
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

# The one marker that escapes the PROPRIETARY class, and it is honoured ONLY in a hand resolution.
#
# A public-domain dedication fits no SPDX identifier: `blessing` is SQLite's "May you do good and
# not evil" licence, whose text is absent from such a file, and CC0-1.0 is a legal instrument that
# a plain dedication does not invoke. So there is nothing to write but a LicenseRef -- which the
# PROPRIETARY pattern catches, and would classify a public-domain package as the exact opposite of
# what it is.
#
# **It is deliberately not honoured when it comes out of a BOM.** `classify` leaves it
# PROPRIETARY; only `classify_resolved` lets it through. Otherwise a package could declare this
# marker about itself and buy a permissive verdict on its own word, which is precisely the kind of
# exception that stops being a measurement. The marker means "someone read the file", and only this
# table records that.
PUBLIC_DOMAIN_MARKER = re.compile(r"^LicenseRef-PublicDomain\b", re.IGNORECASE)

# Free by policy although the family it belongs to is not: MPL-2.0 is in the free tier, MPL
# generally is in the case-by-case tier. Checked BEFORE the classes, because the narrower rule has
# to win over the broader one.
#
# The trailing guard is not decoration. `MPL-2.0-no-copyleft-exception` is a DIFFERENT SPDX licence
# and stays case-by-case, and `\b` alone would match it -- the hyphen after "2.0" is a word
# boundary. Same shape as the missing `\b` that would have read "MITRE" as MIT, one level in:
# an exception carved out of a broader rule needs anchoring at BOTH ends.
FREE_BY_POLICY = re.compile(r"\bMPL-2\.0\b(?!-)", re.IGNORECASE)

# Licenses this script cannot read out of the BOM, resolved by hand at the package itself.
#
# Keyed by (name, version) EXACTLY, and that is the whole design: a version bump drops out of the
# table and the component goes back to UNKNOWN. An exception list matching by name would cover
# versions nobody ever looked at, and silently, which is the usual way such a list stops being
# true. This one expires by itself. Every entry that applies is printed on each run, green or not,
# so it cannot sit here unread.
RESOLVED_BY_HAND = {
    # NOT open source, and the entry exists to say so rather than to clear it. The nuspec declares
    # `<license type="file">LICENSE.txt</license>` -- a file reference, which is why the BOM only
    # ever shows the "Unknown - See URL" placeholder -- and the file in the package (8393 bytes) is
    # "MICROSOFT SOFTWARE LICENSE TERMS / MICROSOFT.DATA.SQLCLIENT.SNI LIBRARY", read out of the
    # nupkg rather than off a web page.
    #
    # Section 3(a) grants redistribution only as "Distributable Code": inside an application and
    # "not as a standalone distribution", and it requires distributors and end users to "agree to
    # terms that protect it at least as much as this agreement". That second condition is the one
    # that matters to a NuGet library: an MIT-licensed package cannot impose it on the people who
    # install it. Test-only here, so nothing is redistributed -- but it arrives with the SQL Server
    # provider, so any package that SHIPS SqlClient has it in its own verdict set.
    #
    # Resolving this to an SPDX id would have been the comfortable move and a false one. It is
    # named, not cleared: the verdict stays a finding.
    ("Microsoft.Data.SqlClient.SNI.runtime", "6.0.2"): (
        "LicenseRef-Microsoft-SqlClient-SNI",
        "Microsoft EULA, read from LICENSE.txt inside the nupkg. Redistribution is conditional "
        "(section 3a: inside an application only, and downstream must be bound to equally "
        "protective terms). NAMED, NOT APPROVED",
    ),
    # The one SHIPPED component that was unnameable, and the first entry here that clears rather
    # than condemns. `<license type="file">LICENSE</license>` again -- the same second case as
    # SqlClient.SNI, which is why the placeholder was never evidence either way.
    #
    # Read from the file in the nupkg (1089 chars, sha256 13e3f30c8400e4f3, "Copyright (c) 2023
    # Vincent Kok"), and checked past recognising the text: all five MIT clauses are present
    # (permission grant, "without restriction", the copyright-notice requirement, AS IS,
    # NONINFRINGEMENT) so it is not a fragment, and there is NOTHING after the closing clause, so
    # it is plain MIT rather than MIT-plus-a-condition. That last check is the one that would have
    # caught SqlClient.SNI, where the comfortable guess was wrong.
    #
    # Measured twice, independently: once elsewhere in the fleet from the network copy and once
    # here from the local package cache, same bytes. The nuspec also pins repository commit
    # 9c6f40725eee9f687e7d369a0bded64664ad38d2 -- an immovable ref, unlike xunit.abstractions,
    # whose declared licence URL points at a moving branch.
    # Public domain, and the thinnest evidence of the round: LICENSE.txt is 61 characters --
    # "SQLite is Public Domain" plus a link (sha256 99464c3a88df7b70, read from the nupkg). So the
    # linked authority was read too, and states it in terms: "All of the code and documentation in
    # SQLite has been dedicated to the public domain by the authors". Measured twice
    # independently, from the network copy elsewhere in the fleet and from the local cache here.
    #
    # CAVEAT THAT MUST NOT BE DROPPED: the file speaks about **SQLite**, not about the
    # **SourceGear build** of it. Compilation, any patches and the packaging are not addressed by
    # it. Acceptable for a test-only dependency, which this is -- it arrives with the SQLite
    # harness. If it ever appeared in the shipped set, someone would have to establish the
    # provenance of that build, and this entry would not be enough.
    ("SourceGear.sqlite3", "3.50.4.5"): (
        "LicenseRef-PublicDomain-SQLite",
        "LICENSE.txt in the nupkg is 61 chars, \"SQLite is Public Domain\" plus a link to "
        "sqlite.org/copyright.html, which states the dedication in terms. NO SPDX id fits: the "
        "blessing text is absent and CC0 is not invoked. Covers SQLite itself, NOT the provenance "
        "of this particular build",
    ),
    ("Mollie.Api", "4.18.0"): (
        "MIT",
        "read from LICENSE inside the nupkg (1089 chars, sha256 13e3f30c8400e4f3). Complete MIT "
        "text, no additional conditions after the closing clause. nuspec pins repository commit "
        "9c6f40725eee9f687e7d369a0bded64664ad38d2 for re-checking",
    ),
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

# The project's licence policy, decided 2026-11-07: three tiers, not two.
#
# Two tiers were the structural defect this replaces. With permissive-or-finding, an LGPL
# dependency that policy wants documented produced the same yellow job as an AGPL one that policy
# wants stopped -- and people stop reading a signal that cannot tell those apart.
#
# STOP is about redistribution being refused or conditional. CASE_BY_CASE is about an obligation
# someone has to write down. UNKNOWN sits in CASE_BY_CASE deliberately: "nobody has read it" is a
# thing to resolve, not a thing to block a release on, and the gate names it on every run until
# somebody does.
STOP = {"PROPRIETARY", "AGPL", "SSPL", "BUSL", "GPL"}
CASE_BY_CASE = {"LGPL", "EPL", "CDDL", "MS-PL", "MPL", UNKNOWN}

# Exit codes, and the gap between 2 and 3 is the point: 2 means NOT MEASURED. Reusing it for a
# policy stop would make "red because the licence is refused" indistinguishable from "red because
# the gate knows nothing", which are opposite problems with opposite fixes. Case-by-case keeps 1,
# so a consumer of this script that has not been updated meets an unknown 3 on a stop licence and
# fails hard, rather than quietly treating it as yellow.
EXIT_FREE, EXIT_CASE_BY_CASE, EXIT_NOT_MEASURED, EXIT_STOP = 0, 1, 2, 3


# Set while the self-test drives main(). main() runs the self-test before doing anything, so
# without this the self-test's own calls to main() would recurse forever. It exists so the cases
# below can cross main()'s boundary rather than re-implementing what main() does with an exception,
# which is the whole point of them.
_SELF_TEST_RUNNING = False


class NotMeasured(Exception):
    """Raised when no verdict can be formed. Never the same thing as a clean verdict."""


def _rank(name):
    return ORDER.index(name)


def _classify_atom(expression, resolved=False):
    """One license expression with no OR and no AND left in it."""
    # Only on the resolved path, and checked before the classes so the PROPRIETARY pattern does not
    # claim it first. See PUBLIC_DOMAIN_MARKER for why a BOM never gets this.
    if resolved and PUBLIC_DOMAIN_MARKER.match(expression.strip()):
        return PERMISSIVE_NAME
    if FREE_BY_POLICY.search(expression):
        return PERMISSIVE_NAME
    for name, pattern, _ in CLASSES:
        if re.search(pattern, expression, re.IGNORECASE):
            return name
    if PERMISSIVE.search(expression):
        return PERMISSIVE_NAME
    return UNKNOWN


def classify(expression, resolved=False):
    """Classify one license expression, honouring OR as a choice and AND as a conjunction.

    `resolved` marks a value that came out of RESOLVED_BY_HAND, and it has to be threaded all the
    way down to the atoms rather than checked on the whole string: a resolution written as a
    bundle, `MIT AND LicenseRef-PublicDomain-SQLite`, would otherwise miss an anchored match on the
    front and fall through to the untrusted path, where the marker is PROPRIETARY and a conjunction
    takes the strongest atom. The entry would then report the opposite of what someone read. No
    entry is a bundle today; this is cheaper than the comment explaining why it must not become one.
    """
    cleaned = expression.replace("(", " ").replace(")", " ").strip()
    if not cleaned:
        return UNKNOWN
    branches = re.split(r"\s+OR\s+", cleaned, flags=re.IGNORECASE)
    branch_ranks = []
    for branch in branches:
        atoms = [a for a in re.split(r"\s+AND\s+", branch, flags=re.IGNORECASE) if a.strip()]
        if not atoms:
            # UNREACHABLE as the splitter stands, and kept deliberately. `cleaned` is stripped and
            # non-empty, and the OR delimiter requires whitespace on both sides and consumes it
            # greedily, so a split can never yield an empty piece: after one match the next
            # character is non-whitespace, so the delimiter cannot match again immediately, and a
            # leading or trailing operator loses its whitespace to strip() and never matches at
            # all. Measured 2026-11-07 by tracing this line against dangling, doubled, empty-paren
            # and leading-operator expressions -- none reach it.
            #
            # It stays because the line below calls min() over a generator, which raises ValueError
            # on an empty one: the guard costs nothing and the crash it prevents would arrive via a
            # regex change, which is exactly the edit that would break the invariant. Marked rather
            # than deleted so it reads as a decision, and marked rather than "covered" because a
            # test that cannot reach it cannot cover it.
            branch_ranks.append(_rank(UNKNOWN))
            continue
        # Everything in a branch applies at once, so the strongest atom decides the branch.
        branch_ranks.append(min(_rank(_classify_atom(atom, resolved)) for atom in atoms))
    # The branches are alternatives we may pick from, so the weakest branch decides.
    return ORDER[max(branch_ranks)]


def classify_resolved(expression):
    """Classify a value that came from RESOLVED_BY_HAND, where the reserved marker is honoured.

    Separate from `classify` on purpose: the exception has to be unavailable to the thing being
    judged. A BOM claiming the public-domain marker about its own component stays PROPRIETARY.
    """
    return classify(expression, resolved=True)


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
    #
    # It may resolve an UNKNOWN in either direction, and both are in use: xunit.abstractions
    # becomes permissive, and SqlClient.SNI becomes PROPRIETARY, which is a finding. "Resolved"
    # here means someone read the licence, not that the component passed.
    resolved = RESOLVED_BY_HAND.get((component.get("name"), component.get("version")))
    if resolved is not None and verdict == UNKNOWN:
        license_name = resolved[0]
        return (classify_resolved(license_name),
                "%s (resolved by hand, was %r)" % (license_name, shown))
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
    shipped_keys = {_key(c) for c in shipped}
    solution_keys = {_key(c) for c in solution}

    # MEMBERSHIP, NOT COUNTS, and the difference is a hole I shipped and then fell into.
    #
    # The loop below iterates the SOLUTION BOM and marks each component shipped or test-only. A
    # component in the shipped BOM but missing from the solution one is therefore never classified
    # at all -- invisible, in the half of the verdict that actually gates. The guard here used to
    # compare sizes (`len(solution) < len(shipped)`), which passes happily at 9 shipped against 58
    # in the solution while one of those 9 is absent. Found by a sabotage run that produced exactly
    # that state by accident: an injected STOP licence in the shipped BOM came back exit 0.
    #
    # The solution BOM is a superset of the shipped one by construction, so any missing key means
    # the two files do not describe what their names claim, and nothing can be concluded.
    missing = sorted(shipped_keys - solution_keys)
    if missing:
        raise NotMeasured(
            "%d shipped component(s) are absent from the solution BOM, so they would never be "
            "classified: %s. The solution BOM must be a superset of the shipped one"
            % (len(missing), ", ".join(missing)))
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
            # PROPRIETARY is in STOP but is not copyleft, so the label is chosen before the set is
            # consulted. A warning that calls a EULA "blocking copyleft" is wrong about the one
            # thing the reader needs from it.
            if verdict == "PROPRIETARY":
                severity = "STOP -- not open source"
            elif verdict in STOP:
                severity = "STOP -- copyleft that binds a redistributed package"
            elif verdict == UNKNOWN:
                severity = "case-by-case -- cannot be named, so nobody has read it yet"
            else:
                severity = "case-by-case -- an obligation to write down, not a stop"
            print("::warning::%s (%s): %s -- %s, %s. %s"
                  % (name, scope, expression, verdict, severity, WHY[verdict]))

    # The closing line is the one somebody reads when deciding what to do first, so it leads with
    # the worst class present rather than with a count. A count puts a vendor EULA and an
    # unreadable placeholder at the same priority, and they are not the same kind of problem: one
    # is a condition on redistributing the package, the other is an open question about it.
    def worst_of(scope):
        return min((verdict for _, verdict, _ in findings[scope]), key=_rank)

    def named(scope, tier):
        return [name for name, verdict, _ in findings[scope] if verdict in tier]

    # Test-only components are reported with their tier but never decide the exit code -- the same
    # scope property the vulnerability scan uses. A copyleft test framework is not redistributed by
    # this package, and a gate permanently red for something we do not ship stops meaning anything.
    for component in named("test-only", STOP):
        print("::warning::%s is test-only, so it does not decide this verdict -- but it is in the "
              "STOP tier and would block a release if this package ever shipped it." % component)

    stopped = named("shipped", STOP)
    if stopped:
        print("::error::STOP: %d shipped component(s) carry a licence the policy refuses: %s"
              % (len(stopped), ", ".join(stopped)))
        print("This fails the build. Redistribution is refused or conditional, which is not "
              "something a release can carry.")
        return EXIT_STOP

    cases = named("shipped", CASE_BY_CASE)
    if cases:
        print("%d shipped component(s) need a documented decision, worst class %s: %s"
              % (len(cases), worst_of("shipped"), ", ".join(cases)))
        print("Reported, not blocking: the policy asks for these to be written down, and the run "
              "keeps naming them until they are.")
        return EXIT_CASE_BY_CASE

    if findings["test-only"]:
        print("Every shipped component is in the free tier. %d test-only component(s) carry "
              "something, worst class %s -- not redistributed here, so not this verdict."
              % (len(findings["test-only"]), worst_of("test-only")))
        return EXIT_FREE
    print("All %d components are in the free tier." % len(solution))
    return EXIT_FREE


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
    # Malformed OR shapes: a dangling operator, empty parentheses, a doubled operator. These pin
    # that the splitter degrades to the readable operand instead of throwing or returning UNKNOWN.
    # They do NOT reach the empty-branch guard in classify() -- see the comment there; a first
    # version of this block claimed they did, which was wrong and is why the claim now names what
    # it was measured with.
    expect("MIT OR ", PERMISSIVE_NAME)
    expect("MIT OR ()", PERMISSIVE_NAME)
    expect("GPL-3.0-only OR ()", "GPL")
    expect("MIT  OR   OR  Apache-2.0", PERMISSIVE_NAME)
    expect(" OR MIT", PERMISSIVE_NAME)
    expect("Apache-2.0", PERMISSIVE_NAME)
    expect("BSD-3-Clause", PERMISSIVE_NAME)
    expect("PostgreSQL", PERMISSIVE_NAME)
    expect("GPL-3.0-only", "GPL")
    expect("AGPL-3.0-or-later", "AGPL")
    expect("LGPL-2.1-only", "LGPL")
    # MPL-2.0 is free by policy; the family is not. Both ends of the carve-out are pinned, because
    # an exception anchored at one end only is how "MITRE" would have read as MIT.
    expect("MPL-2.0", PERMISSIVE_NAME)
    expect("MPL-1.1", "MPL")
    expect("MPL-2.0-no-copyleft-exception", "MPL")   # a DIFFERENT SPDX licence, stays case-by-case
    expect("MPL-2.0 AND GPL-3.0-only", "GPL")        # free atom does not clear a stop atom
    expect("MPL-2.0 OR MPL-1.1", PERMISSIVE_NAME)    # a choice, so take the free branch
    expect("SSPL-1.0", "SSPL")
    expect("MS-PL", "MS-PL")
    expect("LicenseRef-Microsoft-SqlClient-SNI", "PROPRIETARY")
    expect("LicenseRef-Anything-At-All", "PROPRIETARY")   # the prefix, not the vendor's name
    expect("MIT AND LicenseRef-Vendor", "PROPRIETARY")    # both apply, the conditional one binds
    expect("MIT OR LicenseRef-Vendor", PERMISSIVE_NAME)   # a choice, so take the MIT branch
    # The public-domain marker is NOT honoured from a BOM: a package cannot clear itself.
    expect("LicenseRef-PublicDomain-SQLite", "PROPRIETARY")

    # ... and IS honoured from a hand resolution, where someone read the file. Both directions,
    # because an exception carved into a pattern is otherwise the one part nothing checks.
    for expression, wanted in (
        ("LicenseRef-PublicDomain-SQLite", PERMISSIVE_NAME),
        ("LicenseRef-PublicDomain", PERMISSIVE_NAME),
        ("LicenseRef-PublicDomainish-Vendor", "PROPRIETARY"),  # the \b, or this would slip through
        # Threaded through the algebra, not matched on the whole string: a bundle resolution would
        # otherwise miss the anchor and be judged on the untrusted path.
        ("MIT AND LicenseRef-PublicDomain-SQLite", PERMISSIVE_NAME),
        ("LicenseRef-PublicDomain-SQLite AND MIT", PERMISSIVE_NAME),
        ("GPL-3.0-only AND LicenseRef-PublicDomain-SQLite", "GPL"),
        ("LicenseRef-Vendor-X", "PROPRIETARY"),
        ("MIT", PERMISSIVE_NAME),
        ("GPL-3.0-only", "GPL"),
    ):
        actual = classify_resolved(expression)
        if actual != wanted:
            failures.append("classify_resolved(%r) = %r, expected %r"
                            % (expression, actual, wanted))

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
        # A resolution can also make a component WORSE than unknown, and this one does: the entry
        # names a licence without approving it, so the verdict has to stay a finding.
        ({"name": "Microsoft.Data.SqlClient.SNI.runtime", "version": "6.0.2",
          "licenses": [{"license": {"name": "Unknown - See URL"}}]}, "PROPRIETARY"),
        ({"name": "Microsoft.Data.SqlClient.SNI.runtime", "version": "6.0.3",
          "licenses": [{"license": {"name": "Unknown - See URL"}}]}, UNKNOWN),
        # A read public-domain dedication clears, at its exact version.
        ({"name": "SourceGear.sqlite3", "version": "3.50.4.5",
          "licenses": [{"license": {"name": "Unknown - See URL"}}]}, PERMISSIVE_NAME),
        ({"name": "SourceGear.sqlite3", "version": "3.50.4.6",
          "licenses": [{"license": {"name": "Unknown - See URL"}}]}, UNKNOWN),
        # And a component that declares the marker ITSELF does not clear: the BOM is not a reader.
        ({"name": "Self.Declared", "version": "1",
          "licenses": [{"expression": "LicenseRef-PublicDomain-Totally"}]}, "PROPRIETARY"),
    ):
        actual = component_verdict(component)[0]
        if actual != wanted:
            failures.append("component_verdict(%r) = %r, expected %r"
                            % (component, actual, wanted))

    # The scope guard, in both directions AND on the verdict it returns. A count comparison
    # passed the first of these cases. Asserting only "did it raise" would have passed the
    # comfortable repair -- routing every subset to NOT MEASURED -- which is a guard that
    # never lies and also never decides anything.
    import tempfile as _tempfile
    def _write_raw(text):
        handle = _tempfile.NamedTemporaryFile("w", suffix=".json", delete=False, encoding="utf-8")
        handle.write(text)
        handle.close()
        return handle.name

    def _write(components):
        handle = _tempfile.NamedTemporaryFile("w", suffix=".json", delete=False, encoding="utf-8")
        json.dump({"bomFormat": "CycloneDX", "components": components}, handle)
        handle.close()
        return handle.name

    pad = [{"name": "pad%d" % i, "version": "1",
            "licenses": [{"license": {"id": "MIT"}}]} for i in range(5)]
    def _licensed(name, identifier):
        return {"name": name, "version": "1", "licenses": [{"license": {"id": identifier}}]}

    def _licensed_version(name, version, identifier):
        return {"name": name, "version": version,
                "licenses": [{"license": {"id": identifier}}]}

    stop_pkg = _licensed("ghost", "GPL-3.0-only")
    weak_pkg = _licensed("weak", "LGPL-2.1-only")

    # These go through main(), not report(). report() RAISES NotMeasured; the translation into
    # exit 2 lives in main(), so a test that catches the exception and asserts "this means 2" is
    # asserting its own belief about main() and would not notice main() returning 0 instead.
    # Measured 2026-11-07: it would not have. The boundary of the tested region was in the wrong
    # place -- same class as the discarded return value, one step further out.
    # Each case also pins what the run SAYS, because an exit code cannot distinguish two branches
    # that return the same number. Measured 2026-11-07: deleting the test-only STOP warning
    # entirely left this self-test green, and that warning is the only thing that surfaces a vendor
    # EULA sitting in the test tree -- which is exactly the state this repo is in. A branch no test
    # tells apart looks like a safeguard and is not one.
    def _unlicensed(name, version):
        return {"name": name, "version": version}

    # `says` is required phrases, `silent` is forbidden ones. Measured 2026-11-07 by deleting each
    # print branch in report()/main() one at a time: 12 of 18 were removable with nothing
    # noticing. The ones below are pinned because each carries a statement the exit code does not
    # -- the base set beside the zero, the per-component itemisation, the hand resolutions, the
    # reason a tier does or does not fail the build. The only branch deliberately left unpinned is
    # a bare print("") for spacing.
    for shipped_components, solution_components, wanted, says, silent, case in (
        # A shipped component missing from the solution BOM: fewer shipped than solution, so a
        # size check passes, and the component would never be classified.
        ([pad[0], stop_pkg], pad, EXIT_NOT_MEASURED,
         ("NOT MEASURED", "ghost@1", "never be classified", "Nothing was concluded"), (),
         "a shipped component absent from the solution BOM"),
        # The honest shape: shipped is a subset of the solution. The guard must stay quiet -- and
        # the run must still reach a verdict, in every tier.
        ([pad[0]], pad, EXIT_FREE,
         ("free tier", "shipped library gates", "[shipped", "Measured:",
          "Note: the hand resolution"), (),
         "an all-permissive subset"),
        ([stop_pkg], pad + [stop_pkg], EXIT_STOP,
         ("::error::STOP", "ghost", "This fails the build", "::warning::ghost@1 (shipped)"), (),
         "a STOP licence inside an honest subset"),
        ([weak_pkg], pad + [weak_pkg], EXIT_CASE_BY_CASE,
         ("documented decision", "LGPL", "weak", "Reported, not blocking"), (),
         "a case-by-case licence inside an honest subset"),
        # Scope, the other direction: a STOP licence present in the solution but NOT shipped keeps
        # the verdict free. Without this the gate goes permanently red for a test dependency this
        # package does not redistribute, and a permanently red gate is read as noise. The verdict
        # is 0 either way, so only the message can tell this branch from the one above it.
        ([pad[0]], pad + [stop_pkg], EXIT_FREE,
         ("::warning::", "ghost", "test-only, so it does not decide this verdict",
          "Every shipped component is in the free tier", "[test-only"), (),
         "a STOP licence that is test-only"),
        # The hand-resolution table, which nothing reached until the deletion sweep showed its two
        # output branches were both removable. It is the one part of the verdict that rests on
        # somebody having read a licence, so it is also the part most worth a regression.
        ([_unlicensed("xunit.abstractions", "2.0.3")],
         pad + [_unlicensed("xunit.abstractions", "2.0.3")], EXIT_FREE,
         ("Resolved by hand: xunit.abstractions@2.0.3 is Apache-2.0", "resolved by hand, was"),
         (),
         "a hand resolution naming a permissive licence"),
        # The repo's actual state: a vendor EULA arriving through the test project only. PROPRIETARY
        # is a STOP class, so this stays exit 0 solely because of scope -- and says so.
        ([pad[0]], pad + [_unlicensed("Microsoft.Data.SqlClient.SNI.runtime", "6.0.2")], EXIT_FREE,
         ("Resolved by hand: Microsoft.Data.SqlClient.SNI.runtime@6.0.2",
          "NAMED, NOT APPROVED", "test-only, so it does not decide this verdict"), (),
         "a hand-resolved vendor EULA that is test-only"),
        # Same component shipped rather than test-only: naming a licence is not approving it.
        ([_unlicensed("Microsoft.Data.SqlClient.SNI.runtime", "6.0.2")],
         pad + [_unlicensed("Microsoft.Data.SqlClient.SNI.runtime", "6.0.2")], EXIT_STOP,
         ("::error::STOP", "Microsoft.Data.SqlClient.SNI.runtime"), (),
         "a hand-resolved vendor EULA that IS shipped"),
        # An UNKNOWN shipped component. Traced 2026-11-07: the severity line for UNKNOWN had
        # never executed, so the tier that exists precisely for "nobody has read this yet" was
        # reported by an untested branch. A component declaring nothing at all is the usual way to
        # arrive here.
        ([_unlicensed("mystery", "1")], pad + [_unlicensed("mystery", "1")], EXIT_CASE_BY_CASE,
         ("cannot be named, so nobody has read it yet", "<none declared>", "mystery"), (),
         "a shipped component that declares no licence at all"),
        # THE BOM WINS. A hand resolution may only ever replace UNKNOWN; if it could overrule a
        # declared expression it would be a way to silence a real finding, which is the opposite of
        # what the table is for. This component has a hand entry saying Apache-2.0 and a BOM
        # declaring GPL-3.0-only, and the GPL must win. The rule is already pinned at component
        # level above; what was missing is the end-to-end half -- that it also survives scope
        # assignment and reaches the exit code, and that the run does not SAY "resolved by hand"
        # about a component whose licence the BOM named.
        ([_licensed_version("xunit.abstractions", "2.0.3", "GPL-3.0-only")],
         pad + [_licensed_version("xunit.abstractions", "2.0.3", "GPL-3.0-only")], EXIT_STOP,
         ("::error::STOP", "xunit.abstractions"), ("resolved by hand, was",),
         "a declared licence that a hand entry must not be able to override"),
    ):
        shipped_path, solution_path = _write(shipped_components), _write(solution_components)
        quiet, sys.stdout = sys.stdout, io.StringIO()
        global _SELF_TEST_RUNNING
        _SELF_TEST_RUNNING = True
        try:
            actual = main(["license-classify.py", shipped_path, solution_path])
        finally:
            _SELF_TEST_RUNNING = False
            spoken, sys.stdout = sys.stdout.getvalue(), quiet
            os.unlink(shipped_path)
            os.unlink(solution_path)
        if actual != wanted:
            failures.append("scope guard on %s returned exit %r, expected %r"
                            % (case, actual, wanted))
        for phrase in says:
            if phrase not in spoken:
                failures.append("the run on %s exited %r but never said %r, so the branch that "
                                "says it is removable without any test noticing"
                                % (case, actual, phrase))
        for phrase in silent:
            if phrase in spoken:
                failures.append("the run on %s said %r, which it must never say for this input"
                                % (case, phrase))

    # main()'s own argument handling, which nothing else reaches. A mis-invocation must be a hard
    # fail: a gate called with the wrong number of paths has measured nothing, and the one thing it
    # must never do is look like a pass.
    for argv, wanted, case in (
        (["license-classify.py"], EXIT_NOT_MEASURED, "no arguments"),
        (["license-classify.py", "only-one.json"], EXIT_NOT_MEASURED, "one argument"),
        (["license-classify.py", "a", "b", "c"], EXIT_NOT_MEASURED, "too many arguments"),
        (["license-classify.py", "--self-test"], EXIT_FREE, "--self-test"),
    ):
        quiet, sys.stdout = sys.stdout, io.StringIO()
        _SELF_TEST_RUNNING = True
        try:
            actual = main(argv)
        finally:
            _SELF_TEST_RUNNING = False
            spoken, sys.stdout = sys.stdout.getvalue(), quiet
        if actual != wanted:
            failures.append("main() with %s returned exit %r, expected %r" % (case, actual, wanted))
        # A mis-invocation must say so. Exiting 2 in silence is indistinguishable from the BOM
        # guards, and the fix for the two is not the same.
        if wanted == EXIT_NOT_MEASURED and ("usage:" not in spoken
                                             or "--self-test" not in spoken):
            failures.append("main() with %s exited %r without printing the full usage"
                            % (case, actual))

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

    # The three file-level failures, and the cause each one must NAME. Traced 2026-11-07 by
    # recording which lines the self-test executes: all three of these raises had never run. They
    # are the guards this gate leans on hardest -- "I could not read it" must never be reportable
    # as "I found nothing" -- and they had been checked by hand once and never pinned. Naming the
    # cause matters as much as the exit code, because a missing file, an empty one and a truncated
    # one have three different fixes and only the message tells them apart.
    absent = _write_raw("")
    os.unlink(absent)
    for path, phrase, case in (
        (absent, "does not exist", "a BOM that is not there"),
        (_write_raw(""), "is empty", "a zero-byte BOM"),
        (_write_raw('{"bomFormat": "CycloneDX", "compon'), "not readable JSON",
         "a truncated BOM"),
        (_write_raw('"a string, which is valid JSON"'), "top level", "a BOM that is not an object"),
    ):
        try:
            load_components(path, "self-test")
            failures.append("load_components accepted %s" % case)
        except NotMeasured as error:
            if phrase not in str(error):
                failures.append("load_components refused %s but never said %r -- the message is "
                                "what names the fix, and these four have different ones"
                                % (case, phrase))
        if os.path.exists(path):
            os.unlink(path)

    # Every class belongs to exactly one tier. Without this, adding a class and forgetting to tier
    # it would make it a finding that no branch reports and no exit code reflects -- the quiet
    # failure this whole file is built against.
    for name, _, _ in CLASSES:
        in_tiers = (name in STOP) + (name in CASE_BY_CASE)
        if in_tiers != 1:
            failures.append("class %r is in %d tiers, expected exactly 1" % (name, in_tiers))
    if (UNKNOWN in CASE_BY_CASE) is False:
        failures.append("UNKNOWN must be in the case-by-case tier")
    if PERMISSIVE_NAME in STOP or PERMISSIVE_NAME in CASE_BY_CASE:
        failures.append("permissive must be in no tier but free")

    if failures:
        print("::error::License classifier self-test failed -- this gate cannot be trusted:")
        for failure in failures:
            print("  %s" % failure)
        return False
    print("Self-test passed: permissive, copyleft and unnameable licenses are told apart, and an "
          "unusable BOM is told from an empty one.")
    return True


def main(argv):
    # This branch is the one line of the file the self-test cannot reach, by construction: the flag
    # that lets the self-test call main() is exactly what skips it. Named here so it reads as a
    # known exception rather than an untested path nobody noticed.
    if not _SELF_TEST_RUNNING and not self_test():
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
