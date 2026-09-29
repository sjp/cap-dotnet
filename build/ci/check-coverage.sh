#!/usr/bin/env bash
# Merges every Cobertura report under a directory, takes the line and branch rate of one
# package, and fails if either is below its floor, or if any required leg sent no report
# that measured the package.
#
# Usage: check-coverage.sh <report-dir> <package> <min-line-rate> <min-branch-rate> <leg>...
#
# The reports come from every leg of the test matrix, and a line counts as covered if any
# leg ran it. The platform backends are chosen at run time, so each leg reaches only its
# own share of the package; gating one leg alone would hold the others to nothing. For the
# same reason a merge missing a leg is judged on less than it should be, so each <leg> names
# a subdirectory of <report-dir> (one per downloaded artifact) that must hold at least one
# report measuring the package. Lines are matched by their path from src/ onwards, since
# each runner checks the repository out to a different absolute path, and a line's branches
# are taken from the report that covered most of them, since Cobertura records how many
# were taken but not which.
#
# The figures are printed, and also appended to $GITHUB_STEP_SUMMARY when it is set.
set -euo pipefail

if [[ $# -lt 5 ]]; then
  echo "usage: $0 <report-dir> <package> <min-line-rate> <min-branch-rate> <leg>..." >&2
  exit 2
fi

python3 - "$@" <<'PY'
import glob, os, re, sys
import xml.etree.ElementTree as ET

directory, package, min_line, min_branch = sys.argv[1], sys.argv[2], float(sys.argv[3]), float(sys.argv[4])
legs = sys.argv[5:]

reports = sorted(glob.glob(os.path.join(directory, "**", "*.cobertura.xml"), recursive=True))
if not reports:
    sys.exit(f"No Cobertura reports under {directory}.")

hit = {}       # (file, line) -> covered by any report
branches = {}  # (file, line) -> (covered, valid), the best any report saw
measured = set()  # reports that measured the package
condition = re.compile(r"\((\d+)/(\d+)\)")

for report in reports:
    for pkg in ET.parse(report).getroot().iter("package"):
        if pkg.get("name") != package:
            continue
        measured.add(report)
        for cls in pkg.iter("class"):
            path = cls.get("filename", "").replace("\\", "/")
            path = path[path.find("src/"):] if "src/" in path else path
            for line in cls.iter("line"):
                key = (path, int(line.get("number")))
                hit[key] = hit.get(key, False) or int(line.get("hits", "0")) > 0
                match = condition.search(line.get("condition-coverage", ""))
                if line.get("branch") == "True" and match:
                    covered, valid = int(match.group(1)), int(match.group(2))
                    best = branches.get(key, (0, valid))
                    branches[key] = (max(best[0], covered), max(best[1], valid))

if not hit:
    sys.exit(f"None of the {len(reports)} reports measured {package}.")

def leg_reports(leg):
    prefix = os.path.join(directory, leg) + os.sep
    return [r for r in measured if r.startswith(prefix)]

missing = [leg for leg in legs if not leg_reports(leg)]

lines_covered = sum(hit.values())
branches_covered = sum(c for c, _ in branches.values())
branches_valid = sum(v for _, v in branches.values())
line_rate = lines_covered / len(hit)
branch_rate = branches_covered / branches_valid if branches_valid else 1.0

print(f"{package}, merged from {len(reports)} reports:")
print(f"  lines    {line_rate:7.2%}  ({lines_covered}/{len(hit)}), floor {min_line:.2%}")
print(f"  branches {branch_rate:7.2%}  ({branches_covered}/{branches_valid}), floor {min_branch:.2%}")
for leg in legs:
    print(f"  {len(leg_reports(leg)):3} reports from {leg}")

summary = os.environ.get("GITHUB_STEP_SUMMARY")
if summary:
    with open(summary, "a", encoding="utf-8") as out:
        out.write(f"### Coverage of {package}\n\n")
        out.write("| | Merged | Floor |\n|---|---|---|\n")
        out.write(f"| Lines | {line_rate:.2%} ({lines_covered}/{len(hit)}) | {min_line:.2%} |\n")
        out.write(f"| Branches | {branch_rate:.2%} ({branches_covered}/{branches_valid}) | {min_branch:.2%} |\n\n")
        out.write(f"Merged from {len(reports)} reports: ")
        out.write(", ".join(f"{leg} {len(leg_reports(leg))}" for leg in legs) + ".\n")
        if missing:
            out.write(f"\n**No report measuring {package} from:** {', '.join(missing)}.\n")

failed = False
for leg in missing:
    print(f"::error::No report from {leg} measured {package}, so the merge would be judged without it.")
    failed = True
if line_rate < min_line:
    print(f"::error::{package} line coverage {line_rate:.2%} is below the floor of {min_line:.2%}.")
    failed = True
if branch_rate < min_branch:
    print(f"::error::{package} branch coverage {branch_rate:.2%} is below the floor of {min_branch:.2%}.")
    failed = True
sys.exit(1 if failed else 0)
PY
