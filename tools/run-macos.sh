#!/bin/bash
# Use the Windows Steam installation and the launcher in the same CrossOver bottle.
set -euo pipefail
if [[ $# -lt 2 || $# -gt 3 ]]; then
  printf '%s\n' 'Use: bash tools/run-macos.sh "Steam" setup /absolute/path/to/TwinKartPrivate' \
    '     bash tools/run-macos.sh "Steam" launch' >&2
  exit 2
fi
bottle=$1
mode=$2
root="$(cd "$(dirname "$0")/.." && pwd -P)"
exe="$root/build/TwinKartSetup.exe"
wine="${TWIN_KART_CROSSOVER_WINE:-}"
if [[ -z "$wine" ]]; then
  for app in /Applications/CrossOver.app "$HOME/Applications/CrossOver.app" "$HOME/CrossOver.app"; do
    candidate="$app/Contents/SharedSupport/CrossOver/bin/wine"
    if [[ -x "$candidate" ]]; then wine=$candidate; break; fi
  done
fi
if [[ ! -x "$wine" || ! -f "$exe" ]]; then
  printf '%s\n' 'CrossOver or TwinKartSetup.exe is missing. Extract the Windows release and use its tools folder.' >&2
  exit 1
fi
windows_path() {
  local value=${1//\//\\}
  printf 'Z:%s' "$value"
}
case "$mode" in
  setup)
    if [[ $# -ne 3 || ! -d "$3" ]]; then
      printf '%s\n' 'Setup needs the private setup folder from your working Windows installation.' >&2
      exit 2
    fi
    bundle="$(cd "$3" && pwd -P)"
    exec "$wine" --bottle "$bottle" --cx-app "$(windows_path "$exe")" setup --private-data "$(windows_path "$bundle")"
    ;;
  launch)
    [[ $# -eq 2 ]] || exit 2
    exec "$wine" --bottle "$bottle" --cx-app "$(windows_path "$exe")" launch
    ;;
  *) printf '%s\n' 'Specify setup or launch.' >&2; exit 2 ;;
esac
