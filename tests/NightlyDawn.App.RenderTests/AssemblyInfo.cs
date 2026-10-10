using System.Runtime.Versioning;

// Mirrors NightlyDawn.Host.Tests / NightlyDawn.Keys.Tests: this project constructs NightlyDawn.Keys'
// LocalKeyStore/FileKeyFileStore directly (Unix-only), and the CI matrix is ubuntu-latest + macos-latest only
// (plan §1 D1 — Windows is out of scope).
[assembly: UnsupportedOSPlatform("windows")]
