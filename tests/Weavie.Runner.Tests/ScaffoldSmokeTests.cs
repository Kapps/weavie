using Xunit;

namespace Weavie.Runner.Tests;

// Scaffolding placeholder so the new project compiles and runs green: it proves the reference to
// Weavie.Runner resolves and its internal types are visible (InternalsVisibleTo). Replace with real
// coverage — RunnerOptions parsing, BackendManager lifecycle, the ControlApi auth boundary.
public sealed class ScaffoldSmokeTests {
	[Fact]
	public void RunnerInternalsAreVisible() =>
		Assert.Equal("Weavie.Runner", typeof(ControlApi).Assembly.GetName().Name);
}
