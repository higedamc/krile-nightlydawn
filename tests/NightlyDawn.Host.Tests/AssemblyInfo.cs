using System.Runtime.Versioning;

// Mirrors NightlyDawn.Host: macOS and Omarchy (Linux) only, so calling into the host's Unix-only composition code
// does not need a platform guard in every test (CA1416).
[assembly: UnsupportedOSPlatform("windows")]
