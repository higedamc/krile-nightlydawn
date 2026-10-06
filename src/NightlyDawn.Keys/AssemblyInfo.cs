using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

// Krile NightlyDawn targets macOS and Omarchy (Linux) only (plan §1 D1) — declared at
// assembly level so CA1416 doesn't require a platform guard around every Unix-only file
// API call (File.SetUnixFileMode et al.) in a project that will never run on Windows.
[assembly: UnsupportedOSPlatform("windows")]

// Was previously declared in Bech32.cs (moved to NightlyDawn.Core) — tests need this to
// exercise internal types like Nip49KeyEncryption and FileKeyFileStore.
[assembly: InternalsVisibleTo("NightlyDawn.Keys.Tests")]
