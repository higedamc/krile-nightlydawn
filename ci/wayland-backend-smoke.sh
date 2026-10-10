#!/usr/bin/env bash
# Layer 2 smoke test: does Avalonia.Wayland actually start against a real wlroots compositor in CI?
# Avalonia.Headless (tests/NightlyDawn.App.RenderTests) never loads this backend, so this is the only
# evidence there is. Two kinds of proof, on purpose:
#   (i)  the app's own RenderTargetBitmap PNG -- proves the backend initialized and layout ran
#   (ii) a compositor-side grim capture -- proves a frame actually reached the compositor (presented a
#        window). (i) alone cannot show this: offscreen rasterization can succeed with zero windows shown.
# This is an experiment, not a regression gate (see PLANS/KRILE_WAYLAND_BACKEND_SMOKE_LEAF.md). A compositor
# or tool that is missing, or a backend that never starts, is a valid, reportable result -- not a bug in this
# script. Nothing here talks to a real relay (NIGHTLYDAWN_RELAYS is set to a sinkhole by the caller).
set -x

OUT="artifacts/backend-smoke/wayland"
mkdir -p "$OUT"
RESULT="$OUT/RESULT.txt"
: > "$RESULT"

HOST_DLL=$(find src/NightlyDawn.Host/bin/Release -maxdepth 3 -name NightlyDawn.Host.dll | head -1)
if [ -z "$HOST_DLL" ]; then
  echo "FAIL: NightlyDawn.Host.dll not found under src/NightlyDawn.Host/bin/Release (build step must have failed)" | tee -a "$RESULT"
  exit 1
fi
echo "host dll: $HOST_DLL" | tee -a "$RESULT"

COMPOSITOR=""
if command -v cage >/dev/null 2>&1; then
  COMPOSITOR=cage
elif command -v sway >/dev/null 2>&1; then
  COMPOSITOR=sway
fi
echo "compositor chosen: ${COMPOSITOR:-none}" | tee -a "$RESULT"

if [ -z "$COMPOSITOR" ]; then
  echo "RESULT: no compositor binary available (neither cage nor sway installed) -- wayland smoke SKIPPED, not run" | tee -a "$RESULT"
  exit 1
fi

export XDG_RUNTIME_DIR
XDG_RUNTIME_DIR=$(mktemp -d)
chmod 700 "$XDG_RUNTIME_DIR"
export WLR_BACKENDS=headless
export WLR_LIBINPUT_NO_DEVICES=1
# Variable #1 of the single-variable experiment (Lead, PR #17 review): the runner has no GPU
# ("Failed to find any DRM render node" in compositor.log on the first run). These are the documented
# wlroots/Mesa software-rendering escape hatches. If the compositor-side capture is still uniform after
# this, the next single-variable step is swapping cage for sway (cage is a single-client kiosk; sway
# compositors every client, which may matter for a window opened by a process other than cage's own child).
export WLR_RENDERER=pixman
export LIBGL_ALWAYS_SOFTWARE=1
export GALLIUM_DRIVER=llvmpipe

COMPOSITOR_PID=""
cleanup() {
  [ -n "${APP_PID:-}" ] && kill "$APP_PID" 2>/dev/null
  [ -n "$COMPOSITOR_PID" ] && kill "$COMPOSITOR_PID" 2>/dev/null
}
trap cleanup EXIT

if [ "$COMPOSITOR" = "cage" ]; then
  # cage runs exactly one client and exits when it exits, so give it a long-lived dummy client (`sleep`)
  # and connect our own app/grim to the same socket as *additional* clients instead.
  # Must outlive: socket-wait (up to 20s) + settle (2s) + app warmup (3s) + grim poll (up to 30s) +
  # evidence (i)'s own run (up to 20s) -- comfortable margin at 120s.
  timeout 120 cage -d -- sleep 110 > "$OUT/compositor.log" 2>&1 &
else
  timeout 120 sway > "$OUT/compositor.log" 2>&1 &
fi
COMPOSITOR_PID=$!

SOCK=""
for _ in $(seq 1 20); do
  SOCK=$(find "$XDG_RUNTIME_DIR" -maxdepth 1 -name 'wayland-*' ! -name '*.lock' -printf '%f\n' 2>/dev/null | head -1)
  [ -n "$SOCK" ] && break
  sleep 1
done

if [ -z "$SOCK" ]; then
  echo "RESULT: FAIL -- $COMPOSITOR never created a wayland-* socket in \$XDG_RUNTIME_DIR within 20s" | tee -a "$RESULT"
  cat "$OUT/compositor.log" | tee -a "$RESULT" || true
  exit 1
fi

export WAYLAND_DISPLAY="$SOCK"
echo "wayland socket: $WAYLAND_DISPLAY" | tee -a "$RESULT"
sleep 2 # let the compositor finish its own init before anything else tries to connect

# Evidence (ii) first: a long-lived app instance (no NIGHTLYDAWN_SCREENSHOT, so it never self-closes) gives
# grim time to connect and shoot before anything exits.
NIGHTLYDAWN_BACKEND=wayland dotnet "$HOST_DLL" > "$OUT/app-longrun.log" 2>&1 &
APP_PID=$!
sleep 3

# A file existing and being non-empty is not an assertion (the same mistake layer 1's brief called out for
# CaptureRenderedFrame -- a uniform black/blank PNG from a compositor that never actually presented a
# window is a few KB too, grim exits 0, and this is precisely the gap this leaf exists to close). Count
# distinct colors instead.
#
# One grim shot after a fixed sleep is a race, confirmed in practice (Lead, PR #17): two runs with the
# identical software-render fix split 1 PASS / 1 FAIL, no crash in compositor.log either time -- llvmpipe
# is slow, and the first frame isn't always composited within a fixed wait. Poll for up to 30s instead of
# guessing a sleep duration, and record how many seconds it actually took so a slow-but-working compositor
# is distinguishable from one that never presents anything.
MIN_DISTINCT_COLORS=8
POLL_INTERVAL_SECONDS=1
POLL_TIMEOUT_SECONDS=30
EVIDENCE_II=1
if command -v grim >/dev/null 2>&1; then
  START_TS=$(date +%s)
  COLORS=0
  while :; do
    ELAPSED=$(( $(date +%s) - START_TS ))
    grim "$OUT/compositor-side.png" 2> "$OUT/grim.log"
    GRIM_STATUS=$?
    COLORS=0
    if [ "$GRIM_STATUS" -eq 0 ] && [ -s "$OUT/compositor-side.png" ]; then
      COLORS=$(identify -format "%k" "$OUT/compositor-side.png" 2>>"$OUT/grim.log" || echo 0)
    fi
    echo "grim poll at ${ELAPSED}s: exit=$GRIM_STATUS distinct_colors=$COLORS" | tee -a "$RESULT"
    if [ "$COLORS" -ge "$MIN_DISTINCT_COLORS" ] 2>/dev/null; then
      echo "compositor-side.png became non-uniform after ${ELAPSED}s" | tee -a "$RESULT"
      break
    fi
    if [ "$ELAPSED" -ge "$POLL_TIMEOUT_SECONDS" ]; then
      echo "compositor-side.png still uniform after ${POLL_TIMEOUT_SECONDS}s of polling, giving up" | tee -a "$RESULT"
      break
    fi
    sleep "$POLL_INTERVAL_SECONDS"
  done
  if [ "$COLORS" -ge "$MIN_DISTINCT_COLORS" ] 2>/dev/null; then
    echo "RESULT (ii) compositor-side capture: PASS -- $(wc -c < "$OUT/compositor-side.png") bytes, $COLORS distinct colors, settled after ${ELAPSED}s" | tee -a "$RESULT"
    EVIDENCE_II=0
  else
    echo "RESULT (ii) compositor-side capture: FAIL -- only $COLORS distinct color(s) after ${POLL_TIMEOUT_SECONDS}s of polling; looks blank/uniform, no window was actually presented" | tee -a "$RESULT"
  fi
else
  echo "RESULT (ii) compositor-side capture: SKIPPED -- grim not installed" | tee -a "$RESULT"
fi

kill "$APP_PID" 2>/dev/null
wait "$APP_PID" 2>/dev/null
APP_PID=""

# Evidence (i): a fresh short-lived instance with NIGHTLYDAWN_SCREENSHOT set, self-closes after saving.
NIGHTLYDAWN_BACKEND=wayland NIGHTLYDAWN_SCREENSHOT="$PWD/$OUT/app-side.png" \
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

echo "--- compositor.log ---" | tee -a "$RESULT"
cat "$OUT/compositor.log" | tee -a "$RESULT" || true
echo "--- app-longrun.log ---" | tee -a "$RESULT"
cat "$OUT/app-longrun.log" | tee -a "$RESULT" || true

# Both evidence kinds are required for a PASS: (i) alone would also pass for pure offscreen rasterization
# with zero windows ever presented to the compositor, which is exactly the gap this leaf exists to close.
if [ "$EVIDENCE_I" -eq 0 ] && [ "$EVIDENCE_II" -eq 0 ]; then
  echo "RESULT: wayland backend smoke PASS" | tee -a "$RESULT"
  exit 0
fi
echo "RESULT: wayland backend smoke FAIL (see evidence (i)/(ii) above)" | tee -a "$RESULT"
exit 1
