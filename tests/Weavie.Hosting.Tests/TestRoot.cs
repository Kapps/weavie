using System.Runtime.CompilerServices;

namespace Weavie.Hosting.Tests;

/// <summary>
/// Points <c>WEAVIE_ROOT</c> at a throwaway temp dir before any test reads <see cref="Weavie.Core.WeaviePaths"/>,
/// so a run never touches the developer's real <c>~/.weavie</c> (e.g. clobbering a real crash report).
/// </summary>
internal static class TestRoot {
	[ModuleInitializer]
	internal static void Redirect() =>
		Environment.SetEnvironmentVariable(
			"WEAVIE_ROOT", Path.Combine(Path.GetTempPath(), "weavie-hosting-tests"));
}
