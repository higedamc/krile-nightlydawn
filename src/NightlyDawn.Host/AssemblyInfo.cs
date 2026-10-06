using System.Runtime.Versioning;

// Krile NightlyDawn targets macOS and Omarchy (Linux) only (plan §1 D1). Declared at assembly level, like
// NightlyDawn.Keys, so composing the Unix-only key store does not need a platform guard at every call site.
[assembly: UnsupportedOSPlatform("windows")]
