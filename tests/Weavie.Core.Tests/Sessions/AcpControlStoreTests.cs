using Weavie.Core.FileSystem;
using Weavie.Core.Sessions;
using Xunit;

namespace Weavie.Core.Tests;

public sealed class AcpControlStoreTests {
	private const string StorePath = "/config/acp-controls.json";

	[Fact]
	public void AcceptedDefaultsSurviveAStoreReloadAndRemainProviderOwned() {
		var fileSystem = new InMemoryFileSystem();
		var store = new AcpControlStore(fileSystem, StorePath);
		store.Set("codex", "model", "gpt-5.6");
		store.Set("other", "model", "other-model");

		var reloaded = new AcpControlStore(fileSystem, StorePath);

		Assert.Equal("gpt-5.6", reloaded.Resolve("codex")["model"]);
		Assert.Equal("other-model", reloaded.Resolve("other")["model"]);
	}

	[Fact]
	public void MalformedStoreFailsWithoutReplacingOriginalData() {
		var fileSystem = new InMemoryFileSystem([new(StorePath, "{ broken")]);
		var store = new AcpControlStore(fileSystem, StorePath);

		var error = Assert.Throws<AcpControlStoreException>(() => store.Resolve("codex"));

		Assert.Contains(StorePath, error.Message, StringComparison.Ordinal);
		Assert.Equal("{ broken", fileSystem.ReadAllText(StorePath));
	}

	[Fact]
	public void ClearingAStaleValueSurvivesReload() {
		var fileSystem = new InMemoryFileSystem();
		var store = new AcpControlStore(fileSystem, StorePath);
		store.Set("codex", "model", "retired");
		store.Clear("codex", "model", "retired");

		var reloaded = new AcpControlStore(fileSystem, StorePath);

		Assert.Empty(reloaded.Resolve("codex"));
	}

	[Fact]
	public void StaleCleanupDoesNotEraseANewerAcceptedValue() {
		var fileSystem = new InMemoryFileSystem();
		var store = new AcpControlStore(fileSystem, StorePath);
		store.Set("codex", "model", "retired");
		store.Set("codex", "model", "current");

		store.Clear("codex", "model", "retired");

		Assert.Equal("current", store.Resolve("codex")["model"]);
	}
}
