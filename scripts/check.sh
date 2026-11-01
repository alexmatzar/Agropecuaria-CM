#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
export NUGET_HTTP_CACHE_PATH="$PWD/.cache/nuget-http"
dotnet restore Agropecuaria.slnx --locked-mode
dotnet build Agropecuaria.slnx --no-restore
dotnet test Agropecuaria.slnx --no-build --filter 'Category!=FirebaseEmulator&Category!=PostgreSQL&Category!=CloudIntegration'
dotnet restore Agropecuaria.Windows.slnx --locked-mode
dotnet build Agropecuaria.Windows.slnx --no-restore
