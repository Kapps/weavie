using Xunit;

namespace Weavie.Headless.Tests;

// Scaffolding placeholder so the new project compiles and runs green: it proves the reference to
// Weavie.Headless resolves and its internal types are visible (InternalsVisibleTo). Replace with real
// coverage — WebSocketHostBridge fan-out, ListenMode parsing, the headless startup path.
public sealed class ScaffoldSmokeTests {
	[Fact]
	public void HeadlessInternalsAreVisible() =>
		Assert.Equal("Weavie.Headless", typeof(WebSocketHostBridge).Assembly.GetName().Name);
}
