#!/usr/bin/env bash
set -euo pipefail
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"
mkdir -p artifacts/coverage
# A new directory prevents stale runs from entering the merged coverage denominator.
run_dir="$(mktemp -d "$repo_root/artifacts/coverage/run.XXXXXX")"
printf '%s\n' "$run_dir" > artifacts/coverage/current.txt
if dotnet test AllenXeniaOffice.slnx --configuration Release --no-build \
  --collect:'XPlat Code Coverage' --settings coverage.runsettings \
  --results-directory "$run_dir/raw" --logger trx; then
  test_status=0
else
  test_status=$?
fi
# Generate valid partial/failure evidence too, while retaining the test exit status.
dotnet tool run reportgenerator -- \
  "-reports:$run_dir/raw/**/coverage.opencover.xml" \
  "-targetdir:$run_dir/report" \
  '-reporttypes:Html;Cobertura;JsonSummary;TextSummary'
python3 - "$run_dir/report/Cobertura.xml" <<'PY'
import sys
import xml.etree.ElementTree as ET
report = ET.parse(sys.argv[1]).getroot()
expected = {'AllenBuilding.Domain', 'AllenBuilding.Application', 'AllenBuilding.Infrastructure', 'AllenBuilding.Web'}
packages = {p.attrib['name'] for p in report.findall('./packages/package')}
if packages != expected or int(report.attrib.get('lines-valid', '0')) <= 0:
    raise SystemExit('Coverage is missing application modules or source points.')
for source in report.findall('.//class'):
    filename = source.attrib.get('filename', '').replace('\\', '/')
    if '/obj/' in filename or '/Persistence/Migrations/' in filename:
        raise SystemExit('Generated sources unexpectedly entered coverage.')
print(f".NET coverage (C# and Razor): {float(report.attrib['line-rate']) * 100:.2f}% lines; {float(report.attrib['branch-rate']) * 100:.2f}% branches.")
PY
printf 'Coverage report: %s/report/index.html\n' "$run_dir"
exit "$test_status"
