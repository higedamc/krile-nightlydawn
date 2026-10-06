using System.Runtime.Versioning;

// Mirrors NightlyDawn.Keys/AssemblyInfo.cs: this test project calls Unix-only file APIs
// directly (File.GetUnixFileMode in FileKeyFileStoreTests), and the CI matrix is
// ubuntu-latest + macos-latest only (plan §1 D1 — Windows is out of scope).
[assembly: UnsupportedOSPlatform("windows")]
