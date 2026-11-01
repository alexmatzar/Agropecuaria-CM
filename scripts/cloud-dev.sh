#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
export NUGET_HTTP_CACHE_PATH="$PWD/.cache/nuget-http"
export npm_config_cache="$PWD/.cache/npm"
export XDG_CONFIG_HOME="$PWD/.cache/config"
export FIREBASE_CLI_DISABLE_USAGE_REPORTING=true
export ASPNETCORE_ENVIRONMENT=Development
case "${1:-help}" in
  install)
    npm ci --prefix tools/firebase --ignore-scripts --no-fund
    ;;
  firebase)
    arguments=(emulators:start --only auth --project demo-agropecuaria-cm --config tools/firebase/firebase.json --export-on-exit "$PWD/.data/firebase-auth")
    if [[ -d .data/firebase-auth ]]; then arguments+=(--import "$PWD/.data/firebase-auth"); fi
    node tools/firebase/node_modules/firebase-tools/lib/bin/firebase.js "${arguments[@]}"
    ;;
  authority|bootstrap-owner|authority-postgres|bootstrap-owner-postgres|migrate-postgres)
    export FIREBASE_AUTH_EMULATOR_HOST=127.0.0.1:9099
    export Firebase__ProjectId=demo-agropecuaria-cm
    if [[ "$1" == *postgres ]]; then
      export Cloud__UseSqliteDevelopment=false
      export Cloud__DevelopmentPostgresConfig="$PWD/.data/postgres-dev.json"
    else
      export Cloud__UseSqliteDevelopment=true
      export Cloud__DevelopmentDatabase="$PWD/.data/cloud-memberships.dev.db"
    fi
    if [[ "$1" == migrate-postgres ]]; then
      dotnet run --project src/Agropecuaria.Cloud.Api -- --migrate
    elif [[ "$1" == bootstrap-owner* ]]; then
      [[ -t 0 ]] || { printf '%s\n' 'Se requiere terminal interactiva para la cuenta de prueba.' >&2; exit 1; }
      [[ -f .data/development.json ]] || bash scripts/dev.sh init
      export Bootstrap__TenantId="$(ruby -rjson -e 'puts JSON.parse(File.read(".data/development.json")).fetch("Installation").fetch("TenantId")')"
      export Bootstrap__StoreId="$(ruby -rjson -e 'puts JSON.parse(File.read(".data/development.json")).fetch("Installation").fetch("StoreId")')"
      read -r -p 'Correo del propietario de PRUEBA: ' Bootstrap__Email
      read -r -s -p 'Contraseña de PRUEBA (mínimo 15 caracteres): ' AGROPECUARIA_CLOUD_OWNER_PASSWORD; printf '\n'
      export Bootstrap__Email AGROPECUARIA_CLOUD_OWNER_PASSWORD
      trap 'unset AGROPECUARIA_CLOUD_OWNER_PASSWORD' EXIT
      dotnet run --project src/Agropecuaria.Cloud.Api -- --bootstrap-owner
    else
      dotnet run --project src/Agropecuaria.Cloud.Api
    fi
    ;;
  api|api-cloud)
    export Identity__Mode=Firebase
    export Identity__AuthorityEndpoint=http://127.0.0.1:5189/
    export Firebase__EmulatorEndpoint=http://127.0.0.1:9099/
    export Firebase__ApiKey=demo-only
    if [[ "$1" == api-cloud ]]; then export Sync__Enabled=true; export Sync__Mode=Cloud; fi
    bash scripts/dev.sh api
    ;;
  enroll)
    [[ -t 0 ]] || { printf '%s\n' 'Se requiere terminal interactiva.' >&2; exit 1; }
    [[ -f .data/development.json ]] || bash scripts/dev.sh init
    export Identity__AuthorityEndpoint=http://127.0.0.1:5189/
    export Firebase__EmulatorEndpoint=http://127.0.0.1:9099/
    export Firebase__ApiKey=demo-only
    read -r -p 'Correo de la cuenta de PRUEBA: ' AGROPECUARIA_ENROLL_EMAIL
    read -r -s -p 'Contraseña: ' AGROPECUARIA_ENROLL_PASSWORD; printf '\n'
    export AGROPECUARIA_ENROLL_EMAIL AGROPECUARIA_ENROLL_PASSWORD
    trap 'unset AGROPECUARIA_ENROLL_PASSWORD' EXIT
    bash scripts/security.sh enroll
    ;;
  test)
    dotnet test tests/Agropecuaria.Tests --filter 'Category=FirebaseEmulator'
    ;;
  test-postgres)
    export AGROPECUARIA_POSTGRES_CONFIG="$PWD/.data/postgres-dev.json"
    dotnet test tests/Agropecuaria.Tests --filter 'Category=CloudIntegration'
    ;;
  *) printf '%s\n' 'Uso: bash scripts/cloud-dev.sh install|firebase|bootstrap-owner|authority|enroll|api|test|bootstrap-owner-postgres|authority-postgres|migrate-postgres|api-cloud|test-postgres' ;;
esac
