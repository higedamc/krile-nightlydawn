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

## Running the app

> **Changed (leaf 1f):** the executable is now `src/NightlyDawn.Host`, not
> `src/NightlyDawn.App`. If you were told to run `dotnet run --project src/NightlyDawn.App`,
> use the command below instead; the App project is a library and no longer starts.

```
dotnet run --project src/NightlyDawn.Host
```

The window opens with one tab holding one timeline column. `+ Tab` adds a tab,
`+ Column` adds a column to the active tab, and a column's `×` closes it and stops
its subscription. **Neither tabs nor columns are persisted yet** (`IAppStateStore`
is not wired to the shell), so every launch starts from one tab and one column.

Each column holds its own query. Type one, press Subscribe (or Enter),
and public notes from the configured relays stream in; the loading bar clears
once every connected relay has delivered its stored events.

Query syntax is KQL: `from <source> where <expression>`. Both clauses are
mandatory — write `where ()` to match everything the source already selects.

| Source | Meaning | Example |
|---|---|---|
| `home` | notes from your followed authors (kind:3). Needs a signed-in key | `from home where ()` |
| `mentions` | notes mentioning you. Needs a signed-in key | `from mentions where ()` |
| `user("npub1…")` | one author's notes | `from user("npub180cvv0…") where ()` |
| `kind("1")` | notes of one event kind | `from kind("1") where ()` |
| `search("text")` | local substring match on note content | `from search("nostr") where ()` |
| `relay("wss://…")` | notes first seen on one relay | `from relay("wss://relay.damus.io") where ()` |

`where` filters with comparisons on `kind`, `text`, `created_at`, `user.npub`,
`tags.t`, `reply` and `root`, combined with `&` (and), `|` (or) and `!`
(not), e.g. `from home where kind = 1 & reply = true` or
`from home where (kind = 1 | kind = 6) & !reply = true`.

`from list(...)` (NIP-51 lists) is recognized but not yet supported.

Relays: by default `wss://relay.damus.io` and `wss://nos.lol`. Override with a
comma-separated `wss://` list; plaintext `ws://` is refused before any socket opens:

```
NIGHTLYDAWN_RELAYS="wss://relay.damus.io,wss://nos.lol,wss://relay.nostr.band" dotnet run --project src/NightlyDawn.Host
```

Reading needs no key. The **Keys** panel (collapsed, under the timeline) is
optional and independent of the timeline:

- *No key*: enter a passphrase twice and press **Generate key**. The key is
  created in-process and stored only as NIP-49 `ncryptsec1…` ciphertext at
  `~/Library/Application Support/NightlyDawn/local-key.ncryptsec` (macOS) or
  `$XDG_DATA_HOME/NightlyDawn/local-key.ncryptsec` (Linux, fallback
  `~/.local/share`), directory 0700, file 0600. `NIGHTLYDAWN_DATA_DIR` overrides
  the location. **The passphrase is the only thing protecting the key; there is
  no recovery without it.**
- *Key stored, locked* (every new launch): enter the passphrase and press
  **Unlock**. Unlocking does not rewrite the stored file.
- *Unlocked*: the public key is shown as `npub1…` (with a **Copy npub** button; the hex form underneath). **Export (ncryptsec)** re-encrypts
  the key under a passphrase you type and shows the result read-only; nothing
  reaches the clipboard until you press **Copy** (the clipboard is readable by
  other apps on this account and may sync to other devices). **Lock** ends the
  session and keeps the stored key (Unlock brings it back). **Sign out** deletes
  the stored key from this device and asks you to press it twice — export first
  if you want to keep it.

Posting needs an unlocked key. With one, the compose box above the columns has
**Post**, and each note row carries **Reply**, **Quote**, **Repost** and a heart
(kind:7 reaction, content `+`). Without a key store the host prints
`publishing: unavailable (no key store)` at startup and the App reports publishing as
unavailable up front rather than failing after you have typed, so
`[NightlyDawn] publishing: ready` on stderr is the line to check before trying to post.

Two things worth knowing before the first post:

- **Publishing goes to every connected relay in the pool, not to a NIP-65 write list.**
  The default pool is `wss://relay.damus.io` and `wss://nos.lol`; set
  `NIGHTLYDAWN_RELAYS` to publish somewhere else.
- **Reply and Repost only target kind:1 notes**: on a repost row both buttons are
  disabled (`CanReplyOrRepost`), and the Core builder rejects the call outright
  (`ArgumentException`) so the rule does not depend on the UI remembering it. Quote
  and the heart work on any kind. Generic reposts (kind:16) and deletions (kind:5)
  are deliberately not in v1.

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
   dotnet run --project src/NightlyDawn.Host
   ```

   The host picks the **native Wayland backend** when `WAYLAND_DISPLAY` is set
   (Hyprland sets it) and falls back to X11 through XWayland otherwise. To
   compare both paths for the spike:

   ```
   NIGHTLYDAWN_BACKEND=wayland dotnet run --project src/NightlyDawn.Host   # force Wayland
   NIGHTLYDAWN_BACKEND=x11     dotnet run --project src/NightlyDawn.Host   # force X11 / XWayland
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
     AVALONIA_GLOBAL_SCALE_FACTOR=2 NIGHTLYDAWN_BACKEND=x11 dotnet run --project src/NightlyDawn.Host
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
   NIGHTLYDAWN_SCREENSHOT=/tmp/nightlydawn-wayland.png NIGHTLYDAWN_BACKEND=wayland dotnet run --project src/NightlyDawn.Host
   NIGHTLYDAWN_SCREENSHOT=/tmp/nightlydawn-x11.png     NIGHTLYDAWN_BACKEND=x11     dotnet run --project src/NightlyDawn.Host
   ```

## Theme

The palette is Krile's own: a dark-leaning purple, with a light variant of the
same hue. The host follows the OS by default and can be pinned:

```
NIGHTLYDAWN_THEME=dark   dotnet run --project src/NightlyDawn.Host
NIGHTLYDAWN_THEME=light  dotnet run --project src/NightlyDawn.Host
NIGHTLYDAWN_THEME=system dotnet run --project src/NightlyDawn.Host   # the default: follow the OS
```

The app prints `theme: system|light|dark` at startup, and says so loudly if the
variable holds something it does not recognise (it then follows the OS, rather
than silently pretending the setting worked).

**There is no in-app toggle yet, and nothing is persisted**: the variant lives
for the run. A toggle belongs to the window shell, which is being rewritten for
tabs and columns, and persistence belongs with the rest of the app state
(`IAppStateStore`); both are separate pieces of work on purpose.

The colours live in `src/NightlyDawn.App/Themes/` and no view opts into them:
`KrileTheme.axaml` holds the palette per variant (including the Fluent keys the
stock controls already read), and `KrileStyles.axaml` paints the few surfaces
that need to be Krile's rather than Fluent's. `NightlyDawn.App.RenderTests`
renders the real window in both variants and asserts on the pixels, so CI's
artifact carries a light and a dark screenshot of every pull request.

## License

Krile StarryEyes is licensed under the MIT/X11 License — see `LICENSE.TXT`.
NightlyDawn's new code is licensed under the same terms. See `NOTICE.md` for
provenance and third-party license details.

(C)2013 Karno. (C)2026 Krile NightlyDawn contributors. Other contributors have
some rights of code fragments — see the commit log for detail.
