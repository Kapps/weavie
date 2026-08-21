using System.Runtime.CompilerServices;

namespace Weavie.Hosting.Tests;

/// <summary>Raises the test process's open-file soft limit once at assembly load — the same call
/// <see cref="HostCore"/> makes for the real app. Without it the suite's ~20 real <c>SettingsStore</c>
/// instances (temp-file-backed, some with file watchers) run at the CI runner's default `ulimit -n`, and
/// cumulative descriptor pressure across xunit's parallelized test classes can exhaust it — surfacing as
/// "Too many open files" in whichever test happens to write a settings file when the budget runs out, rather
/// than as a leak in that test.</summary>
internal static class PosixFileLimitWarmup {
	[ModuleInitializer]
	internal static void Warm() => PosixFileLimit.RaiseToHardLimit(_ => { });
}
