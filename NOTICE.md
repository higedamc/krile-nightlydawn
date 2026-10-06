# NOTICE

Krile NightlyDawn is a fork of **Krile StarryEyes** by Karno
(https://github.com/karno/StarryEyes), licensed under the MIT License,
Copyright (c) 2013 Karno. See `LICENSE.TXT` for the full license text, kept
verbatim from the upstream project.

This fork's new code (everything under `src/NightlyDawn.*` and
`tests/NightlyDawn.*`) is Copyright (c) 2026 Krile NightlyDawn contributors,
also under the MIT License.

## What is new vs. ported

- **New**: the entire Nostr integration (`NightlyDawn.Nostr`), the Avalonia UI
  shell (`NightlyDawn.App`), and the storage layer (`NightlyDawn.Storage`).
  Twitter-specific code (`Anomaly`, `Cadena`, and the Twitter object model in
  `StarryEyes/Models`) is not reused.
- **Ported** (planned, phase 1 — design carried over, implementation
  rewritten against .NET 8): the KQL (Krile Query Language) filter grammar
  and expression model (`StarryEyes/Filters`) into `NightlyDawn.Filters`; the
  cache/CRUD *design* of `StarryEyes.Casket` (not its EF6 code) into
  `NightlyDawn.Storage`; general-purpose helpers from `StarryEyes.Albireo`
  where platform-neutral; the settings, key-assign, and theme *models* from
  `StarryEyes/Models`; the tab/column model.
- **Not reused**: the WPF view layer, `StarryEyes.Nightmare` (Win32 interop),
  `SweetMagic` (Windows updater), `Detective` (Windows crash reporter),
  `MahApps.Metro` (WPF-only theme library), the `krile.ico` application icon,
  and the `kriletan.png` mascot artwork — the icon and mascot may carry
  separate artist rights beyond the code's MIT license, so neither is carried
  into this fork. NightlyDawn uses new branding.

## Attribution

> Krile NightlyDawn — a fork of Krile StarryEyes by Karno (MIT).

## Third-party licenses (new code)

Recorded here as dependencies are introduced in later phases. As of this
commit, the `NightlyDawn.*` projects have no third-party package
dependencies beyond the .NET SDK and xUnit (test-only, MIT License).
