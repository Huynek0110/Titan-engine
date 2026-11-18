#!/usr/bin/env bash
# Stress test cho SkiaSharp soundbar renderer thread-safety / segfault.
# Chạy sequential baseline (13 styles x 30 frames) rồi so sánh vs parallel (4 tasks).
# Exit code: so khung khac (0 = sach); segfault = exit 139.
#
# Usage: ./scripts/stress-soundbar.sh [repeatCount] [roundsPerTask]
#
set -euo pipefail

export PATH="$HOME/.dotnet:$PATH"
export DOTNET_ROOT="$HOME/.dotnet"

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJ="$ROOT/tests/SoundbarThreadSafety/SoundbarThreadSafety.csproj"

cd "$ROOT"

REPEAT="${1:-1}"
ROUNDS="${2:-20}"

echo "=== Soundbar stress test ==="
echo "Project: $PROJ"
echo "Repeat: $REPEAT  Rounds/task: $ROUNDS"
echo

dotnet build "$PROJ" -c Release --no-incremental >/tmp/stress-build.log 2>&1 || {
  echo "BUILD FAILED:"; cat /tmp/stress-build.log; exit 2
}

dotnet run --project "$PROJ" -c Release --no-build -- "$REPEAT" "$ROUNDS" 2>&1 | tee /tmp/stress-run.log

EXIT=${PIPESTATUS[0]}
echo
echo "Exit code: $EXIT"
if [ "$EXIT" -eq 139 ]; then
  echo "SEGFAULT DETECTED (exit 139)"
elif [ "$EXIT" -eq 0 ]; then
  echo "ALL CLEAN"
else
  echo "MISMATCHES or ERROR (exit $EXIT)"
fi
exit "$EXIT"
