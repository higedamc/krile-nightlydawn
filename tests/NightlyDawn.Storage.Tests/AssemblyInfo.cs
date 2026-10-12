using System.Runtime.Versioning;

// Mirrors NightlyDawn.Keys.Tests / NightlyDawn.App.RenderTests: JsonAppStateStore is Unix-only (it reuses
// NightlyDawn.Keys' NativeFileSync and sets Unix file modes), and the CI matrix is ubuntu-latest + macos-latest
// only (plan §1 D1 -- Windows is out of scope).
[assembly: UnsupportedOSPlatform("windows")]
