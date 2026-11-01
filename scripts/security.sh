#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
export AGROPECUARIA_WORKSPACE="$PWD"
export NUGET_HTTP_CACHE_PATH="$PWD/.cache/nuget-http"
dotnet run --project tools/Agropecuaria.Security.Cli -- "$@"
