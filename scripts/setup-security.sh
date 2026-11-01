#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
if [[ ! -t 0 ]]; then printf '%s\n' 'Ejecuta este asistente en una terminal interactiva.' >&2; exit 1; fi
trap 'unset AGROPECUARIA_MASTER_PASSWORD confirmation' EXIT
if [[ ! -f .data/development.json ]]; then bash scripts/dev.sh init; fi
if [[ ! -f .data/security/keyring.json ]]; then
  read -r -p 'Ruta NUEVA para la clave de recuperación (guardar fuera del equipo): ' recovery_path
  read -r -s -p 'Frase maestra de esta Mac (mínimo 20 caracteres): ' AGROPECUARIA_MASTER_PASSWORD; printf '\n'
  read -r -s -p 'Repite la frase maestra: ' confirmation; printf '\n'
  [[ "$confirmation" == "$AGROPECUARIA_MASTER_PASSWORD" ]] || { printf '%s\n' 'Las frases no coinciden.' >&2; exit 1; }
  export AGROPECUARIA_MASTER_PASSWORD
  bash scripts/security.sh init "$recovery_path"
else
  printf '%s\n' 'Se conserva el sobre existente. La frase se solicitará al iniciar la API.'
fi
printf '%s\n' 'Claves locales preparadas. Las cuentas y roles se aprovisionan en la autoridad de nube; no se crearon usuarios locales.'
printf '%s\n' 'Para la demostración sin recursos reales, usa scripts/cloud-dev.sh. Conserva la recuperación fuera de este equipo.'
