using Weavie.Core.FileSystem;
using Weavie.Core.Workflows;
using Xunit;

namespace Weavie.Core.Tests;

public sealed class WorkflowDecisionHistoryTests : IDisposable {
	private readonly TempDirectory _dir = new("weavie-decision-history");

	private string FilePath => _dir.Combine("decision-history.json");

	[Fact]
	public void KeepsTheMostRecentDecisionsAcrossReloads() {
		var history = new WorkflowDecisionHistory(new LocalFileSystem(), FilePath);
		int changes = 0;
		history.Changed += () => changes++;

		for (int i = 0; i < WorkflowDecisionHistory.Capacity + 3; i++) {
			history.Append(i % 2 == 0 ? "agent" : "openai", TimeSpan.FromMilliseconds(i));
		}

		var reloaded = new WorkflowDecisionHistory(new LocalFileSystem(), FilePath).Recent;
		Assert.Equal(WorkflowDecisionHistory.Capacity + 3, changes);
		Assert.Equal(WorkflowDecisionHistory.Capacity, reloaded.Count);
		Assert.Equal(new WorkflowDecisionRecord("openai", 3), reloaded[0]);
		Assert.Equal(new WorkflowDecisionRecord("agent", WorkflowDecisionHistory.Capacity + 2), reloaded[^1]);
	}

	[Fact]
	public void MalformedFileStartsEmpty() {
		File.WriteAllText(FilePath, "{not json");

		Assert.Empty(new WorkflowDecisionHistory(new LocalFileSystem(), FilePath).Recent);
		Assert.True(File.Exists(FilePath + ".bad"));
	}

	public void Dispose() => _dir.Dispose();
}
