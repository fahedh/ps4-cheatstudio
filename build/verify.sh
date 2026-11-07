#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
dotnet restore PS4GoldHENCheatStudio.sln --locked-mode
dotnet test tests/Studio.Tests -c Release --logger 'trx;LogFileName=core.trx' --results-directory artifacts/test-results
dotnet build src/Studio.Desktop -c Release --no-restore
