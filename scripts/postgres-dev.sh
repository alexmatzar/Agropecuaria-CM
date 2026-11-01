#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
umask 077
version=18.6
prefix="$PWD/.cache/postgresql/$version"
cluster="$PWD/.data/postgresql"
credentials="$PWD/.data/postgres-dev.json"
instance="agro-cm-pg-$(python3 -c 'import hashlib,os; print(hashlib.sha256(os.getcwd().encode()).hexdigest()[:12])')"
case "${1:-help}" in
  install)
    [[ -x "$prefix/bin/postgres" ]] && { "$prefix/bin/postgres" --version; exit 0; }
    build="$PWD/.cache/postgresql-build"
    mkdir -p "$build"
    archive="$build/postgresql-$version.tar.bz2"
    [[ -f "$archive" ]] || curl --fail --location --retry 2 --output "$archive" "https://ftp.postgresql.org/pub/source/v$version/postgresql-$version.tar.bz2"
    printf '%s  %s\n' '555610c24d53e4316da5b7d3fc25c279d96856d5e0e23ee308c328c5fa881d9f' "$archive" | shasum -a 256 --check
    [[ -d "$build/postgresql-$version" ]] || tar -xjf "$archive" -C "$build"
    cd "$build/postgresql-$version"
    ./configure --prefix="$prefix" --without-icu --without-readline --without-zlib > ../configure.log 2>&1 || { tail -n 30 ../configure.log >&2; exit 1; }
    make -j8 > ../build.log 2>&1 || { tail -n 30 ../build.log >&2; exit 1; }
    make install > ../install.log 2>&1
    "$prefix/bin/postgres" --version
    ;;
  start|docker-start)
    if [[ "$1" == start ]]; then [[ -x "$prefix/bin/initdb" ]] || { printf '%s\n' 'Primero ejecuta postgres-dev.sh install.' >&2; exit 1; }; fi
    mkdir -p .data
    if [[ ! -f "$credentials" ]]; then
      [[ ! -d "$cluster" ]] || { printf '%s\n' 'Existe un cluster sin su configuración; no se reemplaza.' >&2; exit 1; }
      if [[ "$1" == docker-start ]] && { docker container inspect "$instance" > /dev/null 2>&1 || docker volume inspect "$instance" > /dev/null 2>&1; }; then
        printf '%s\n' 'Hay datos PostgreSQL existentes pero falta su configuración. No se generan credenciales nuevas ni se reemplaza el volumen.' >&2; exit 1
      fi
      python3 - "$credentials" <<'PY'
import json, secrets, socket, sys
from pathlib import Path
with socket.socket() as sock:
    sock.bind(('127.0.0.1', 0)); port=sock.getsockname()[1]
with Path(sys.argv[1]).open('x') as file:
    json.dump({'port':port, 'username':'agro_dev', 'password':secrets.token_hex(32), 'database':'postgres'},file)
PY
    fi
    port="$(python3 -c 'import json; print(json.load(open(".data/postgres-dev.json"))["port"])')"
    if [[ "$1" == docker-start ]]; then
      if docker container inspect "$instance" > /dev/null 2>&1; then
        owner="$(docker inspect --format '{{index .Config.Labels "com.agropecuaria.workspace"}}' "$instance")"
        [[ "$owner" == "$PWD" ]] || { printf '%s\n' 'El nombre de contenedor pertenece a otra instalación.' >&2; exit 1; }
        docker start "$instance" > /dev/null
      else
        export POSTGRES_PASSWORD="$(python3 -c 'import json; print(json.load(open(".data/postgres-dev.json"))["password"])')"
        trap 'unset POSTGRES_PASSWORD' EXIT
        docker run --detach --name "$instance" --label "com.agropecuaria.workspace=$PWD" --publish "127.0.0.1:$port:5432" --env POSTGRES_PASSWORD --env POSTGRES_USER=agro_dev --env POSTGRES_DB=postgres --mount "type=volume,source=$instance,target=/var/lib/postgresql" postgres:18.6@sha256:3a4264520e121176619f54ff867d12774bc81e95e7b84db58d63bccdb6144b81
        unset POSTGRES_PASSWORD
        trap - EXIT
      fi
      for attempt in {1..30}; do
        if docker exec "$instance" pg_isready -U agro_dev -d postgres > /dev/null 2>&1; then
          printf '%s\n' 'PostgreSQL de pruebas listo en Docker, enlazado solo a loopback.'
          exit 0
        fi
        sleep 1
      done
      printf '%s\n' 'PostgreSQL no respondió dentro de 30 segundos.' >&2; exit 1
    fi
    if [[ ! -d "$cluster" ]]; then
      password_file="$(mktemp "$PWD/.data/postgres-password.XXXXXX")"
      trap 'rm -f "$password_file"' EXIT
      python3 -c 'import json; print(json.load(open(".data/postgres-dev.json"))["password"])' > "$password_file"
      "$prefix/bin/initdb" -D "$cluster" -U agro_dev --encoding=UTF8 --locale=C --auth=scram-sha-256 --pwfile="$password_file" > .data/postgres-init.log
      rm -f "$password_file"
      trap - EXIT
    fi
    if ! "$prefix/bin/pg_ctl" -D "$cluster" status > /dev/null 2>&1; then
      "$prefix/bin/pg_ctl" -D "$cluster" -l "$PWD/.data/postgres.log" -o "-h 127.0.0.1 -p $port -k ''" -w start
    fi
    printf '%s\n' 'PostgreSQL de desarrollo listo en loopback. Credenciales aleatorias en .data/postgres-dev.json (no imprimir ni versionar).'
    ;;
  stop)
    if [[ -d "$cluster" ]] && "$prefix/bin/pg_ctl" -D "$cluster" status > /dev/null 2>&1; then "$prefix/bin/pg_ctl" -D "$cluster" -m fast -w stop; fi
    if docker container inspect "$instance" > /dev/null 2>&1; then
      owner="$(docker inspect --format '{{index .Config.Labels "com.agropecuaria.workspace"}}' "$instance")"
      [[ "$owner" == "$PWD" ]] && docker stop "$instance"
    fi
    ;;
  test)
    export AGROPECUARIA_POSTGRES_CONFIG="$credentials"
    export NUGET_HTTP_CACHE_PATH="$PWD/.cache/nuget-http"
    dotnet test tests/Agropecuaria.Tests --filter 'Category=PostgreSQL'
    ;;
  *) printf '%s\n' 'Uso: bash scripts/postgres-dev.sh install|start|docker-start|stop|test' ;;
esac
