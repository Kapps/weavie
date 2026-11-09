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
	public void Summary_CountsAgentAnsweredDecisionsAtTheirMedian() {
		var history = new WorkflowDecisionHistory(new LocalFileSystem(), FilePath);
		Assert.Equal(new DecisionHistorySummary(0, TimeSpan.Zero), history.Summary());

		foreach (int ms in new[] { 9000, 1200, 1400 }) {
			history.Append("agent", TimeSpan.FromMilliseconds(ms));
		}
		history.Append("openai", TimeSpan.FromMilliseconds(200));
		Assert.Equal(new DecisionHistorySummary(3, TimeSpan.FromMilliseconds(1400)), history.Summary());

		history.Append("agent", TimeSpan.FromMilliseconds(1000));
		Assert.Equal(new DecisionHistorySummary(4, TimeSpan.FromMilliseconds(1300)), history.Summary());
	}

	[Fact]
	public void MalformedFileStartsEmpty() {
		File.WriteAllText(FilePath, "{not json");

		Assert.Empty(new WorkflowDecisionHistory(new LocalFileSystem(), FilePath).Recent);
		Assert.True(File.Exists(FilePath + ".bad"));
	}

	public void Dispose() => _dir.Dispose();
}
