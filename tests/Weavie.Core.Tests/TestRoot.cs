using System.Runtime.CompilerServices;

namespace Weavie.Core.Tests;

/// <summary>
/// Points <c>WEAVIE_ROOT</c> at a throwaway temp dir before any test reads <see cref="Weavie.Core.WeaviePaths"/>,
/// so a run never touches the developer's real <c>~/.weavie</c> (e.g. the resolver's tools-dir probe).
/// </summary>
internal static class TestRoot {
	[ModuleInitializer]
	internal static void Redirect() =>
		Environment.SetEnvironmentVariable(
			"WEAVIE_ROOT", Path.Combine(Path.GetTempPath(), "weavie-core-tests"));
}
