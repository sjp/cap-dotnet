#!/usr/bin/env bash
# Merges every Cobertura report under a directory, takes the line and branch rate of one
# package, and fails if either is below its floor.
#
# Usage: check-coverage.sh <report-dir> <package> <min-line-rate> <min-branch-rate>
#
# The reports come from every leg of the test matrix, and a line counts as covered if any
# leg ran it. The platform backends are chosen at run time, so each leg reaches only its
# own share of the package; gating one leg alone would hold the others to nothing. Lines
# are matched by their path from src/ onwards, since each runner checks the repository out
# to a different absolute path, and a line's branches are taken from the report that
# covered most of them, since Cobertura records how many were taken but not which.
set -euo pipefail

if [[ $# -ne 4 ]]; then
  echo "usage: $0 <report-dir> <package> <min-line-rate> <min-branch-rate>" >&2
  exit 2
fi

python3 - "$@" <<'PY'
import glob, os, re, sys
import xml.etree.ElementTree as ET

directory, package, min_line, min_branch = sys.argv[1], sys.argv[2], float(sys.argv[3]), float(sys.argv[4])

reports = sorted(glob.glob(os.path.join(directory, "**", "*.cobertura.xml"), recursive=True))
if not reports:
    sys.exit(f"No Cobertura reports under {directory}.")

hit = {}       # (file, line) -> covered by any report
branches = {}  # (file, line) -> (covered, valid), the best any report saw
condition = re.compile(r"\((\d+)/(\d+)\)")

for report in reports:
    for pkg in ET.parse(report).getroot().iter("package"):
        if pkg.get("name") != package:
            continue
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

lines_covered = sum(hit.values())
branches_covered = sum(c for c, _ in branches.values())
branches_valid = sum(v for _, v in branches.values())
line_rate = lines_covered / len(hit)
branch_rate = branches_covered / branches_valid if branches_valid else 1.0

print(f"{package}, merged from {len(reports)} reports:")
print(f"  lines    {line_rate:7.2%}  ({lines_covered}/{len(hit)}), floor {min_line:.2%}")
print(f"  branches {branch_rate:7.2%}  ({branches_covered}/{branches_valid}), floor {min_branch:.2%}")

failed = False
if line_rate < min_line:
    print(f"::error::{package} line coverage {line_rate:.2%} is below the floor of {min_line:.2%}.")
    failed = True
if branch_rate < min_branch:
    print(f"::error::{package} branch coverage {branch_rate:.2%} is below the floor of {min_branch:.2%}.")
    failed = True
sys.exit(1 if failed else 0)
PY
