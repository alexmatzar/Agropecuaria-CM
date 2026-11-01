#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
export ASPNETCORE_ENVIRONMENT=Development
export AGROPECUARIA_WORKSPACE="$PWD"
export NUGET_HTTP_CACHE_PATH="$PWD/.cache/nuget-http"
if [[ "${1:-api}" == api && -z "${AGROPECUARIA_MASTER_PASSWORD:-}" && -f .data/security/keyring.json ]]; then
  if [[ ! -t 0 ]]; then printf '%s\n' 'Define AGROPECUARIA_MASTER_PASSWORD de forma segura o ejecuta en una terminal interactiva.' >&2; exit 1; fi
  read -r -s -p 'Frase maestra de esta Mac: ' AGROPECUARIA_MASTER_PASSWORD; printf '\n'
  export AGROPECUARIA_MASTER_PASSWORD
  trap 'unset AGROPECUARIA_MASTER_PASSWORD' EXIT
fi
case "${1:-api}" in
  init) dotnet run --project src/Agropecuaria.LocalApi -- --init-dev ;;
  api) dotnet run --project src/Agropecuaria.LocalApi ;;
  receiver) dotnet run --project tools/Agropecuaria.SyncReceiver.Dev ;;
  test) dotnet test Agropecuaria.slnx --filter 'Category!=FirebaseEmulator&Category!=PostgreSQL&Category!=CloudIntegration' ;;
  *) printf '%s\n' 'Uso: bash scripts/dev.sh init|api|receiver|test' >&2; exit 2 ;;
esac
