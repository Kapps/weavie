using System.Runtime.CompilerServices;

namespace Weavie.Hosting.Tests;

/// <summary>Raises the test process's open-file soft limit once at assembly load — the same call
/// <see cref="HostCore"/> makes for the real app. Without it the suite's ~20 real <c>SettingsStore</c>
/// instances (temp-file-backed, some with file watchers) run at the CI runner's default `ulimit -n`, and
/// cumulative descriptor pressure across xunit's parallelized test classes can exhaust it (see
/// docs/specs/e2e-flake-analysis.md-adjacent flake: Linux run 29973556071, 2026-07-23, `TerminalControllerResyncTests
/// .ShellOutputDuringResync_ReachesThePageExactlyOnce_ViaTheReplay` failed with "Too many open files" writing
/// settings.toml.tmp — not a leak in that test, the assembly's shared FD budget ran out under it).</summary>
internal static class PosixFileLimitWarmup {
	[ModuleInitializer]
	internal static void Warm() => PosixFileLimit.RaiseToHardLimit(_ => { });
}
