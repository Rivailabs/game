#!/usr/bin/env bash
# Offline build and test of everything in astra-kingdoms that runs without Unity or a phone:
# rules, meta, modes, world, client core, Unity compile checks (against stubs), the release tool,
# and the art placeholder checks. Used by .github/workflows/ci.yml and runnable locally.
#
#   ci/build-and-test.sh            # from anywhere
set -euo pipefail
AK="$(cd "$(dirname "$0")/.." && pwd)"
cd "$AK"

dotnet build AstraKingdoms.sln -c Release -warnaserror
dotnet test AstraKingdoms.sln -c Release --no-build --logger "trx;LogFilePrefix=ak" --results-directory "$AK/.build/test-results"

if python3 -c "import numpy, PIL, pytest" 2>/dev/null; then
  python3 art/tools/check_briefs.py
  python3 -m pytest -q -p no:cacheprovider art/tests
else
  echo "SKIP art checks: pip install -r art/requirements.txt"
fi
