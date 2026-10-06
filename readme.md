# Krile NightlyDawn

Krile NightlyDawn is a multi-column, keyboard-centric Nostr client for macOS and
Linux (Omarchy/Hyprland). It is a fork of Krile StarryEyes by Karno (MIT),
rebuilt on .NET 8 and Avalonia UI with a Nostr core in place of the original
Twitter integration.

This is a fork of [Krile StarryEyes](https://github.com/karno/StarryEyes) by
Karno (MIT). The original project is a Windows/.NET Framework Twitter client,
archived since 2018. NightlyDawn keeps the product ideas worth keeping
(multi-column timelines, KQL filter expressions, multi-account, key-assign,
themes) and reimplements them against Nostr instead of the Twitter API. See
`NOTICE.md` for what is ported versus newly written, and the design plan for
the full rationale and phased rollout.

## Status

Early bootstrap. The `NightlyDawn.*` projects under `src/` and `tests/` are the
new Nostr client; nothing under the repository root predating this fork (the
original `StarryEyes`, `Cadena`, `Anomaly`, etc. trees) is part of it — that
code remains for historical reference until the new client reaches parity.

## Target platforms

macOS and Linux (Omarchy / Hyprland via Avalonia's X11/XWayland backend).
Windows is out of scope.

## Building

```
dotnet build NightlyDawn.sln
dotnet test NightlyDawn.sln
```

Requires the .NET 8 SDK (see `global.json`).

## Running the app shell

The app is a minimal Avalonia shell for now (a window with a platform check
panel). It opens with:

```
dotnet run --project src/NightlyDawn.App
```

### macOS

Install the .NET 8 SDK (Homebrew: `brew install --cask dotnet-sdk@8`, or the
installer from https://dotnet.microsoft.com/download/dotnet/8.0). Then run the
command above. Avalonia uses its native macOS backend; nothing else is needed.

### Running on Omarchy (Arch Linux / Hyprland)

1. Install the .NET 8 SDK. Either from the Arch repos:

   ```
   sudo pacman -S dotnet-sdk-8.0
   ```

   or user-local with Microsoft's install script (no root, lands in `~/.dotnet`).
   Download it to a file, look at it, then run it — do not pipe it into a shell:

   ```
   curl -sSL -o /tmp/dotnet-install.sh https://dot.net/v1/dotnet-install.sh
   less /tmp/dotnet-install.sh      # inspect; it should only call dotnetcli.azureedge.net / builds.dotnet.microsoft.com
   bash /tmp/dotnet-install.sh --channel 8.0
   export DOTNET_ROOT="$HOME/.dotnet"
   export PATH="$HOME/.dotnet:$PATH"
   ```

   Check with `dotnet --version` (8.0.x).

2. Native libraries. Avalonia's Linux backends need `fontconfig`, `libwayland`
   and `libxkbcommon` (Wayland path: keyboard layout and IME input) and
   `libx11`, `libice`, `libsm` (X11/XWayland fallback). On Omarchy these are
   normally already present; if not:

   ```
   sudo pacman -S --needed fontconfig wayland libxkbcommon libx11 libice libsm
   ```

3. Run from a Hyprland session:

   ```
   dotnet run --project src/NightlyDawn.App
   ```

   The shell picks the **native Wayland backend** when `WAYLAND_DISPLAY` is set
   (Hyprland sets it) and falls back to X11 through XWayland otherwise. To
   compare both paths for the spike:

   ```
   NIGHTLYDAWN_BACKEND=wayland dotnet run --project src/NightlyDawn.App   # force Wayland
   NIGHTLYDAWN_BACKEND=x11     dotnet run --project src/NightlyDawn.App   # force X11 / XWayland
   ```

   The X11 path requires XWayland to be enabled in Hyprland (`xwayland { enabled = true }`,
   the default).

4. What to check (the "Platform check" panel in the window shows the live values):

   - **Backend actually in use**: the panel prints `WAYLAND_DISPLAY` / `DISPLAY`
     and the `NIGHTLYDAWN_BACKEND` override. Under native Wayland the window has
     client-side decorations drawn by Avalonia; under XWayland Hyprland decorates it.
   - **HiDPI / fractional scaling**: `Render scaling` should match your monitor
     scale (e.g. `2` or `1.5`) and text should be crisp. On the X11 path XWayland
     cannot do fractional scaling itself; if the window renders small or blurry,
     set the scale explicitly:

     ```
     AVALONIA_GLOBAL_SCALE_FACTOR=2 NIGHTLYDAWN_BACKEND=x11 dotnet run --project src/NightlyDawn.App
     ```

     (per-screen: `AVALONIA_SCREEN_SCALE_FACTORS="eDP-1=2;DP-1=1.5"`). Hyprland's
     `xwayland { force_zero_scaling = true }` plus the env var above is the usual
     combination for crisp XWayland apps.
   - **Clipboard**: type in the text box, press Copy, paste into another app;
     copy something elsewhere and press Paste. Both directions should work on
     Wayland and on XWayland.
   - **IME** (Japanese input): typing with fcitx5/ibus into the text box. Report
     whether the preedit shows inline.

   Please report the panel text plus a screenshot for each backend; that is the
   Phase 0 spike result the plan asks for. If no screenshot tool is handy, the app
   can render itself to a PNG and exit:

   ```
   NIGHTLYDAWN_SCREENSHOT=/tmp/nightlydawn-wayland.png NIGHTLYDAWN_BACKEND=wayland dotnet run --project src/NightlyDawn.App
   NIGHTLYDAWN_SCREENSHOT=/tmp/nightlydawn-x11.png     NIGHTLYDAWN_BACKEND=x11     dotnet run --project src/NightlyDawn.App
   ```

## License

Krile StarryEyes is licensed under the MIT/X11 License — see `LICENSE.TXT`.
NightlyDawn's new code is licensed under the same terms. See `NOTICE.md` for
provenance and third-party license details.

(C)2013 Karno. (C)2026 Krile NightlyDawn contributors. Other contributors have
some rights of code fragments — see the commit log for detail.
