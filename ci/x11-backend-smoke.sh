#!/usr/bin/env bash
# Layer 2 smoke test, X11 fallback path (NIGHTLYDAWN_BACKEND=x11, Avalonia's own XWayland/X11 backend
# under Xvfb). Same two-kinds-of-evidence shape as ci/wayland-backend-smoke.sh: (i) the app's own
# RenderTargetBitmap PNG, (ii) a capture taken from outside the app (ImageMagick's `import -window root`)
# proving a frame actually reached the X server, not just an offscreen render. Experiment, not a gate --
# see ci/wayland-backend-smoke.sh's header and PLANS/KRILE_WAYLAND_BACKEND_SMOKE_LEAF.md.
set -x

OUT="artifacts/backend-smoke/x11"
mkdir -p "$OUT"
RESULT="$OUT/RESULT.txt"
: > "$RESULT"

HOST_DLL=$(find src/NightlyDawn.Host/bin/Release -maxdepth 3 -name NightlyDawn.Host.dll | head -1)
if [ -z "$HOST_DLL" ]; then
  echo "FAIL: NightlyDawn.Host.dll not found under src/NightlyDawn.Host/bin/Release (build step must have failed)" | tee -a "$RESULT"
  exit 1
fi
echo "host dll: $HOST_DLL" | tee -a "$RESULT"

if ! command -v Xvfb >/dev/null 2>&1; then
  echo "RESULT: no Xvfb binary available -- x11 smoke SKIPPED, not run" | tee -a "$RESULT"
  exit 1
fi

export DISPLAY=:99
XVFB_PID=""
APP_PID=""
cleanup() {
  [ -n "$APP_PID" ] && kill "$APP_PID" 2>/dev/null
  [ -n "$XVFB_PID" ] && kill "$XVFB_PID" 2>/dev/null
}
trap cleanup EXIT

timeout 60 Xvfb "$DISPLAY" -screen 0 1280x800x24 > "$OUT/xvfb.log" 2>&1 &
XVFB_PID=$!

READY=1
for _ in $(seq 1 20); do
  if [ -e "/tmp/.X11-unix/X99" ]; then
    READY=0
    break
  fi
  sleep 1
done
if [ "$READY" -ne 0 ]; then
  echo "RESULT: FAIL -- Xvfb never created /tmp/.X11-unix/X99 within 20s" | tee -a "$RESULT"
  cat "$OUT/xvfb.log" | tee -a "$RESULT" || true
  exit 1
fi
sleep 1

# Evidence (ii) first: long-lived instance (no NIGHTLYDAWN_SCREENSHOT) so `import` has a window to shoot.
NIGHTLYDAWN_BACKEND=x11 dotnet "$HOST_DLL" > "$OUT/app-longrun.log" 2>&1 &
APP_PID=$!
sleep 3

# A file existing and being non-empty is not an assertion -- count distinct colors, same as the wayland
# script, so a root window that is just empty Xvfb background (no window ever mapped) cannot pass.
MIN_DISTINCT_COLORS=8
EVIDENCE_II=1
if command -v import >/dev/null 2>&1; then
  import -display "$DISPLAY" -window root "$OUT/compositor-side.png" 2> "$OUT/import.log"
  IMPORT_STATUS=$?
  echo "import exit status: $IMPORT_STATUS" | tee -a "$RESULT"
  if [ "$IMPORT_STATUS" -eq 0 ] && [ -s "$OUT/compositor-side.png" ]; then
    COLORS=$(identify -format "%k" "$OUT/compositor-side.png" 2>>"$OUT/import.log" || echo 0)
    echo "compositor-side.png distinct colors: $COLORS" | tee -a "$RESULT"
    if [ "$COLORS" -ge "$MIN_DISTINCT_COLORS" ] 2>/dev/null; then
      echo "RESULT (ii) X-server-side capture: PASS -- $(wc -c < "$OUT/compositor-side.png") bytes, $COLORS distinct colors" | tee -a "$RESULT"
      EVIDENCE_II=0
    else
      echo "RESULT (ii) X-server-side capture: FAIL -- only $COLORS distinct color(s); looks blank/uniform, no window was actually mapped" | tee -a "$RESULT"
    fi
  else
    echo "RESULT (ii) X-server-side capture: FAIL -- see import.log" | tee -a "$RESULT"
  fi
else
  echo "RESULT (ii) X-server-side capture: SKIPPED -- ImageMagick's import not installed" | tee -a "$RESULT"
fi

kill "$APP_PID" 2>/dev/null
wait "$APP_PID" 2>/dev/null
APP_PID=""

# Evidence (i): fresh short-lived instance with NIGHTLYDAWN_SCREENSHOT set, self-closes after saving.
NIGHTLYDAWN_BACKEND=x11 NIGHTLYDAWN_SCREENSHOT="$PWD/$OUT/app-side.png" \
  timeout 20 dotnet "$HOST_DLL" > "$OUT/app-selfshot.log" 2>&1
SELFSHOT_STATUS=$?
echo "self-screenshot run exit status: $SELFSHOT_STATUS" | tee -a "$RESULT"
EVIDENCE_I=1
if [ -s "$OUT/app-side.png" ]; then
  COLORS_I=$(identify -format "%k" "$OUT/app-side.png" 2>>"$OUT/app-selfshot.log" || echo 0)
  echo "app-side.png distinct colors: $COLORS_I" | tee -a "$RESULT"
  if [ "$COLORS_I" -ge "$MIN_DISTINCT_COLORS" ] 2>/dev/null; then
    echo "RESULT (i) app-side RenderTargetBitmap: PASS -- $(wc -c < "$OUT/app-side.png") bytes, $COLORS_I distinct colors" | tee -a "$RESULT"
    EVIDENCE_I=0
  else
    echo "RESULT (i) app-side RenderTargetBitmap: FAIL -- only $COLORS_I distinct color(s); layout rendered blank/uniform" | tee -a "$RESULT"
  fi
else
  echo "RESULT (i) app-side RenderTargetBitmap: FAIL -- see app-selfshot.log" | tee -a "$RESULT"
fi

echo "--- xvfb.log ---" | tee -a "$RESULT"
cat "$OUT/xvfb.log" | tee -a "$RESULT" || true
echo "--- app-longrun.log ---" | tee -a "$RESULT"
cat "$OUT/app-longrun.log" | tee -a "$RESULT" || true

if [ "$EVIDENCE_I" -eq 0 ] && [ "$EVIDENCE_II" -eq 0 ]; then
  echo "RESULT: x11 backend smoke PASS" | tee -a "$RESULT"
  exit 0
fi
echo "RESULT: x11 backend smoke FAIL (see evidence (i)/(ii) above)" | tee -a "$RESULT"
exit 1
