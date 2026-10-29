#!/usr/bin/env bash
# Builds everything (Windows projects cross-compile) and runs all tests that work without
# Windows or a scanner: .NET unit/API tests plus JS client unit and dev-host end-to-end tests.
set -euo pipefail
cd "$(dirname "$0")/.."

dotnet build papertrail.slnx -c Release
dotnet test papertrail.slnx -c Release --no-build

PAPERTRAIL_DEVHOST_DLL="$PWD/src/Papertrail.DevHost/bin/Release/net10.0/Papertrail.DevHost.dll" \
  node --test "clients/js/test/*.test.mjs"
