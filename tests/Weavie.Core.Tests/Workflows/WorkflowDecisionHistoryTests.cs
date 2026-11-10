using Weavie.Core.FileSystem;
using Weavie.Core.Workflows;
using Xunit;

namespace Weavie.Core.Tests;

public sealed class WorkflowDecisionHistoryTests : IDisposable {
	private readonly TempDirectory _dir = new("weavie-decision-history");

	private string FilePath => _dir.Combine("decision-history.json");

	[Fact]
	public void KeepsTheLastTwentyAcrossReloadsAtTheirMedian() {
		var history = new WorkflowDecisionHistory(new LocalFileSystem(), FilePath);
		int changes = 0;
		history.Changed += () => changes++;
		Assert.Equal(new DecisionHistorySummary(0, TimeSpan.Zero), history.Summary());

		history.Append(TimeSpan.Zero);
		history.Append(TimeSpan.FromMilliseconds(1));
		history.Append(TimeSpan.FromMilliseconds(5));
		Assert.Equal(new DecisionHistorySummary(3, TimeSpan.FromMilliseconds(1)), history.Summary());

		// 0, 1, 5, then 3..22 ms: the oldest three drop out, leaving 3..22 with a median of 12.5.
		for (int i = 3; i < 23; i++) {
			history.Append(TimeSpan.FromMilliseconds(i));
		}

		Assert.Equal(23, changes);
		Assert.Equal(new DecisionHistorySummary(20, TimeSpan.FromMilliseconds(12.5)),
			new WorkflowDecisionHistory(new LocalFileSystem(), FilePath).Summary());
		history.Append(TimeSpan.FromMilliseconds(9000));
		Assert.Equal(new DecisionHistorySummary(20, TimeSpan.FromMilliseconds(13.5)), history.Summary());
	}

	[Fact]
	public void MalformedFileStartsEmpty() {
		File.WriteAllText(FilePath, "{not json");

		Assert.Equal(0, new WorkflowDecisionHistory(new LocalFileSystem(), FilePath).Summary().AgentAnswered);
		Assert.True(File.Exists(FilePath + ".bad"));
	}

	public void Dispose() => _dir.Dispose();
}
