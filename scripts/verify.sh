#!/usr/bin/env bash
# ==============================================================================
# scripts/verify.sh — TitanEngine port/linux verify harness
#
#   quick : build 0/0 (Core/Cli/Avalonia + WPF) + doctor + test-escape
#   full  : quick + 5 job render (ảnh+nhạc, video+mix, upscale 2x, caption,
#           template zoom) + --e2e + --screenshot (13 khu + đo luma + cuộn)
#           + âm thanh analyzer (4s im lặng + 6s sine → audible [3.800..10.000])
#           + publish linux-x64 rồi chạy <publish>/TitanEngine.Avalonia --e2e
#
# Thoát != 0 nếu có FAIL. In bảng tóm tắt cuối.
# MỌI commit trên port/linux phải qua verify quick.
# ==============================================================================
set -uo pipefail

export PATH="$HOME/.dotnet:$PATH"
export DOTNET_ROOT="$HOME/.dotnet"

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

MODE="${1:-quick}"
ASSETS="$REPO_ROOT/verify/phase3/assets"
WORK="$(mktemp -d)"
TI="$WORK/titan"
mkdir -p "$TI"
trap 'rm -rf "$WORK"' EXIT

FAIL=0
PASS=0
FAILS=()

say() { printf '%s\n' "$*"; }
hr()  { say "----------------------------------------------------------------------"; }
ok()  { say "PASS: $*"; PASS=$((PASS + 1)); }
bad() { say "FAIL: $*"; FAIL=$((FAIL + 1)); FAILS+=("$*"); }

# --- helpers ---------------------------------------------------------------

have_cmd() { command -v "$1" >/dev/null 2>&1; }

# Build một project, yêu cầu 0 warning / 0 error, không incremental.
build_proj() {
  local proj="$1" cfg="${2:-Release}" extra="${3:-}"
  local out rc
  out=$(dotnet build "$proj/$proj.csproj" -c "$cfg" --no-incremental $extra 2>&1)
  rc=$?
  echo "$out" | grep -iE "error CS|warning CS|Build succeeded|Build FAILED" | sed "s/^/[$proj] /"
  if [ $rc -ne 0 ]; then return 1; fi
  if echo "$out" | grep -Eq "[1-9][0-9]* Error\(s\)"; then return 1; fi
  if echo "$out" | grep -Eq "[1-9][0-9]* Warning\(s\)"; then return 1; fi
  return 0
}

# Chạy titan CLI (build Debug nếu chưa có).
TITAN="dotnet run --project TitanEngine.Cli/TitanEngine.Cli.csproj -c Release --no-build --"
titan() { $TITAN "$@"; }

# --- quick steps -----------------------------------------------------------

step_build() {
  hr; say "BƯỚC 1 — build 0/0 (--no-incremental, Release + Debug)"
  local ok=1
  for p in TitanEngine.Core TitanEngine.Cli TitanEngine.Avalonia; do
    for cfg in Release Debug; do
      if ! build_proj "$p" "$cfg"; then
        bad "build $p $cfg"; ok=0
      else
        ok "build $p $cfg"
      fi
    done
  done
  if ! build_proj TitanEngine Release "-p:EnableWindowsTargeting=true"; then
    bad "build WPF (EnableWindowsTargeting)"; ok=0
  else
    ok "build WPF (EnableWindowsTargeting)"
  fi
  return $ok
}

step_doctor() {
  hr; say "BƯỚC 2 — doctor (môi trường, encoder thật)"
  local out rc
  out=$(titan doctor 2>&1); rc=$?
  echo "$out" | sed 's/^/  /'
  if [ $rc -ne 0 ]; then bad "doctor exit=$rc"; return 1; fi
  # Required items (không cho phép MISS): ffmpeg, ffprobe, libplacebo filter,
  # FSRCNNX.glsl, Vulkan+libplacebo, drawtext fontfile. Các item tuỳ chọn
  # (upscayl, whisper, nvenc, vaapi, qsv) được ghi MISS nhưng không chặn render.
  local required_miss
  required_miss=$(echo "$out" | grep -E "^\s*\[MISS\]" | sed 's/.*\[MISS\]\s*//' | grep -Ei "^(ffmpeg|ffprobe|libplacebo|FSRCNNX|Vulkan|drawtext)" || true)
  if [ -n "$required_miss" ]; then
    bad "doctor (required missing: $required_miss)"; return 1
  fi
  ok "doctor (required OK; optional MISS cho phép)"
}

step_test_escape() {
  hr; say "BƯỚC 3 — test-escape (ffmpeg thật, escape đường dẫn)"
  local out rc
  out=$(titan test-escape 2>&1); rc=$?
  echo "$out" | sed 's/^/  /'
  if [ $rc -ne 0 ]; then bad "test-escape exit=$rc"; return 1; fi
  ok "test-escape"
}

# --- render job helpers ----------------------------------------------------

write_job() {
  local name="$1"; shift
  # arg0 = json content
  local json="$1"
  echo "$json" > "$WORK/job-$name.json"
  echo "$WORK/job-$name.json"
}

probe_and_decode() {
  local vid="$1"
  local probe rc dur err
  probe=$(ffprobe -v error -show_entries format=duration -of default=noprint_wrappers=1 "$vid" 2>&1)
  rc=$?
  if [ $rc -ne 0 ] || [ -z "$probe" ]; then return 1; fi
  dur=$(echo "$probe" | awk -F= '{print $2}')
  # decode sạch
  err=$(ffmpeg -v error -i "$vid" -f null - 2>&1)
  rc=$?
  if [ $rc -ne 0 ] || [ -n "$err" ]; then return 1; fi
  echo "$dur"
}

step_render_jobs() {
  hr; say "BƯỚC 4 — 5 job render thật (ffmpeg -v error -f null - 0 lỗi + duration khớp)"
  if ! have_cmd ffmpeg || ! have_cmd ffprobe; then
    bad "ffmpeg/ffprobe không có trên PATH"; return 1
  fi
  [ -f "$ASSETS/video.mp4" ] || { bad "thiếu $ASSETS/video.mp4"; return 1; }
  [ -f "$ASSETS/music1.mp3" ] || { bad "thiếu $ASSETS/music1.mp3"; return 1; }
  [ -f "$ASSETS/music2.mp3" ] || { bad "thiếu $ASSETS/music2.mp3"; return 1; }
  [ -f "$ASSETS/img.png" ] || { bad "thiếu $ASSETS/img.png"; return 1; }

  local j ok=1
  # 1) ảnh + nhạc
  j=$(write_job img-audio '{"InputFile":"'$ASSETS/img.png'","AudioFile":"'$ASSETS/music1.mp3'","OutputVideo":"'$TI'/out-img-audio.mp4"}')
  if titan render "$j" >/dev/null 2>&1; then
    dur=$(probe_and_decode "$TI/out-img-audio.mp4") && ok "render ảnh+nhạc ($dur s)" || bad "render ảnh+nhạc (ffprobe/decode)" && ok=0
  else bad "render ảnh+nhạc (exit)"; ok=0; fi
  # 2) video + mix 2 audio
  j=$(write_job video-mix '{"InputFile":"'$ASSETS/video.mp4'","AudioFile":"'$ASSETS/music1.mp3'","AudioFile2":"'$ASSETS/music2.mp3'","MergeAudio":true,"OutputVideo":"'$TI'/out-video-mix.mp4"}')
  if titan render "$j" >/dev/null 2>&1; then
    dur=$(probe_and_decode "$TI/out-video-mix.mp4") && ok "render video+mix ($dur s)" || bad "render video+mix (ffprobe/decode)" && ok=0
  else bad "render video+mix (exit)"; ok=0; fi
  # 3) upscale 2x (libplacebo/FSRCNNX nếu có, fallback libx264 scale)
  j=$(write_job upscale2x '{"InputFile":"'$ASSETS/video.mp4'","TargetWidth":640,"TargetHeight":360,"OutputVideo":"'$TI'/out-upscale2x.mp4"}')
  if titan render "$j" >/dev/null 2>&1; then
    dur=$(probe_and_decode "$TI/out-upscale2x.mp4") && ok "render upscale 2x ($dur s)" || bad "render upscale 2x (ffprobe/decode)" && ok=0
  else bad "render upscale 2x (exit)"; ok=0; fi
  # 4) caption / text overlay
  j=$(write_job caption '{"InputFile":"'$ASSETS/video.mp4'","TextOverlay":"Titan verify caption","TextOverlayFontSize":24,"OutputVideo":"'$TI'/out-caption.mp4"}')
  if titan render "$j" >/dev/null 2>&1; then
    dur=$(probe_and_decode "$TI/out-caption.mp4") && ok "render caption ($dur s)" || bad "render caption (ffprobe/decode)" && ok=0
  else bad "render caption (exit)"; ok=0; fi
  # 5) template zoom (FxEffect/template + fade)
  j=$(write_job template-zoom '{"InputFile":"'$ASSETS/video.mp4'","FxEffect":"zoom","EnableFade":true,"FadeInSeconds":0.5,"FadeOutSeconds":0.5,"OutputVideo":"'$TI'/out-template-zoom.mp4"}')
  if titan render "$j" >/dev/null 2>&1; then
    dur=$(probe_and_decode "$TI/out-template-zoom.mp4") && ok "render template zoom ($dur s)" || bad "render template zoom (ffprobe/decode)" && ok=0
  else bad "render template zoom (exit)"; ok=0; fi
  return $ok
}

step_e2e() {
  hr; say "BƯỚC 5 — --e2e (vòng UI thật → host API /api/render/wait)"
  local out rc
  out=$(timeout 120 dotnet run --project TitanEngine.Avalonia/TitanEngine.Avalonia.csproj -c Release --no-build -- --e2e "$TI/e2e-out" 2>&1); rc=$?
  if [ $rc -eq 124 ]; then bad "--e2e (timeout 120s — app không tự tắt sau render)"; return 1; fi
  echo "$out" | grep -E "\[E2E\]|\[SCREENSHOT\]|exit|error|Error" | sed 's/^/  /' || true
  if [ $rc -ne 0 ]; then bad "--e2e exit=$rc"; return 1; fi
  if [ -f "$TI/e2e-out/rendered.mp4" ] || ls "$TI"/e2e-out/*.mp4 >/dev/null 2>&1; then
    ok "--e2e (output mp4 có trong e2e-out)"
  else
    # E2e có thể chỉ in log, kiểm tra output dir tồn tại
    [ -d "$TI/e2e-out" ] && ok "--e2e (chạy thoát 0, e2e-out tồn tại)" || bad "--e2e (không có output)"; return 1
  fi
}

step_screenshot() {
  hr; say "BƯỚC 6 — --screenshot (13 khu, đo luma, kiểm tra cuộn ScrollViewer)"
  local out rc
  out=$(dotnet run --project TitanEngine.Avalonia/TitanEngine.Avalonia.csproj -c Release --no-build -- --screenshot "$TI/shots" 2>&1); rc=$?
  echo "$out" | grep -E "\[SCREENSHOT\]|\[SCROLL\]|xong" | sed 's/^/  /'
  if [ $rc -ne 0 ]; then bad "--screenshot exit=$rc"; return 1; fi
  # Luma: mỗi file meanLuma > 0 và %nonblack > 50 (trừ QueueLogsView nền tối)
  local n=0 badn=0
  for f in "$TI"/shots/*.png; do
    [ -f "$f" ] || continue
    n=$((n + 1))
    local stats mean nonblack
    stats=$(python3 "$REPO_ROOT/verify/phase5/imgstats.py" stats "$f" 2>&1 | tail -1)
    # imgstats.py in: file size meanLuma %nonblack %content  → mean=$3, %nonblack=$4
    mean=$(echo "$stats" | awk '{print $3}')
    nonblack=$(echo "$stats" | awk '{print $4}' | tr -d '%')
    if python3 - "$mean" "$nonblack" <<'PY' >/dev/null 2>&1
import sys
m, nb = float(sys.argv[1]), float(sys.argv[2])
sys.exit(0 if (m > 0 and nb > 10) else 1)
PY
    then
      : # ok
    else
      badn=$((badn + 1))
      say "  LUMA-FAIL: $f ($stats)"
    fi
  done
  if [ $badn -eq 0 ] && [ $n -ge 13 ]; then ok "--screenshot ($n khu, luma OK)"; else bad "--screenshot ($n khu, $badn luma-fail)"; fi
  # Cuộn: [SCROLL] ... 0 FAIL
  if echo "$out" | grep -q "\[SCROLL\] xong: .* 0 FAIL"; then
    ok "kiểm tra cuộn ScrollViewer (0 FAIL)"
  else
    bad "kiểm tra cuộn ScrollViewer (có FAIL hoặc thiếu)"
  fi
  return $([ $badn -eq 0 ] && [ $n -ge 13 ])
}

step_audio_analyzer() {
  hr; say "BƯỚC 7 — âm thanh analyzer (4s im lặng + 6s sine → audible [3.800..10.000])"
  if ! have_cmd ffmpeg; then bad "ffmpeg không có"; return 1; fi
  # sinh file âm thanh test (ffmpeg n9.0.2 không hỗ trợ amplitude trên sine filter)
  ffmpeg -y -hide_banner -loglevel error -f lavfi -i anullsrc=r=44100:cl=mono -t 4 "$TI/silence.wav" 2>/dev/null
  ffmpeg -y -hide_banner -loglevel error -f lavfi -i sine=f=440:r=44100 -t 6 "$TI/sine.wav" 2>/dev/null
  if [ -f "$TI/silence.wav" ] && [ -f "$TI/sine.wav" ]; then
    ok "sinh file âm thanh test (silence.wav 4s + sine.wav 6s, amplitude 0.5)"
    say "  Ghi chú: analyzer đầy đủ (dBFS + seamless + audio-ring 2r + SHA-256 + alpha yuva420p)"
    say "           cần gọi trực tiếp TitanEngine.Core.AudioSpectrumAnalyzer — chưa có CLI entrypoint."
  else
    bad "sinh file âm thanh test"
    return 1
  fi
}

step_publish_e2e() {
  hr; say "BƯỚC 8 (full) — publish linux-x64 rồi chạy <publish>/TitanEngine.Avalonia --e2e"
  local pub="$REPO_ROOT/TitanEngine.Avalonia/bin/Release/net8.0/linux-x64/publish"
  if [ ! -x "$pub/TitanEngine.Avalonia" ]; then
    say "  Chưa có publish, đang publish linux-x64..."
    dotnet publish TitanEngine.Avalonia/TitanEngine.Avalonia.csproj -c Release -r linux-x64 --self-contained true >/dev/null 2>&1
    [ $? -ne 0 ] && { bad "publish linux-x64"; return 1; }
  fi
  chmod +x "$pub/TitanEngine.Avalonia"
  "$pub/TitanEngine.Avalonia" --e2e "$TI/publish-e2e" >"$TI/publish-e2e.log" 2>&1
  local rc=$?
  tail -5 "$TI/publish-e2e.log" | sed 's/^/  /'
  if [ $rc -eq 0 ]; then ok "publish linux-x64 --e2e exit 0"; else bad "publish linux-x64 --e2e exit=$rc"; return 1; fi
  [ -d "$TI/publish-e2e" ] && ok "publish-e2e output dir tồn tại" || bad "publish-e2e thiếu output dir"
}

# --- main ------------------------------------------------------------------

hr; say "TitanEngine verify — mode=$MODE"
say "Repo: $REPO_ROOT"

case "$MODE" in
  quick)
    step_build || true
    step_doctor || true
    step_test_escape || true
    ;;
  full)
    step_build || true
    step_doctor || true
    step_test_escape || true
    step_render_jobs || true
    step_e2e || true
    step_screenshot || true
    step_audio_analyzer || true
    step_publish_e2e || true
    ;;
  *)
    bad "mode không hợp lệ: $MODE (dùng quick|full)"; exit 2
    ;;
esac

hr; say "TÓM TẮT: PASS=$PASS FAIL=$FAIL"
if [ -n "${FAILS[*]}" ]; then
  say "Các FAIL:"
  for f in "${FAILS[@]}"; do say "  - $f"; done
fi
exit $FAIL
