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

## License

Krile StarryEyes is licensed under the MIT/X11 License — see `LICENSE.TXT`.
NightlyDawn's new code is licensed under the same terms. See `NOTICE.md` for
provenance and third-party license details.

(C)2013 Karno. (C)2026 Krile NightlyDawn contributors. Other contributors have
some rights of code fragments — see the commit log for detail.
