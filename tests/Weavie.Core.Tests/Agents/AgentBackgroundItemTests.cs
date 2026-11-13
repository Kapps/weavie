using Weavie.Core.Agents;
using Xunit;

namespace Weavie.Core.Tests;

public sealed class AgentBackgroundItemTests {
	[Theory]
	[InlineData(AgentBackgroundKind.Turn, AgentBackgroundState.Running, true)]
	[InlineData(AgentBackgroundKind.Turn, AgentBackgroundState.Completed, false)]
	[InlineData(AgentBackgroundKind.Turn, AgentBackgroundState.Stopped, false)]
	[InlineData(AgentBackgroundKind.Turn, AgentBackgroundState.Failed, true)]
	[InlineData(AgentBackgroundKind.Task, AgentBackgroundState.Completed, true)]
	public void AnEndedTurnOfTheAgentsOwnIsListedOnlyWhenItFailed(AgentBackgroundKind kind, AgentBackgroundState state, bool listed) {
		var item = new AgentBackgroundItem {
			Id = "task:1",
			Kind = kind,
			Name = "Claude is working",
			Type = "turn",
			Detail = null,
			LastActivity = null,
			Summary = null,
			OutputPath = null,
			Usage = null,
			State = state,
			CanStop = false,
			StartedAtMs = 1,
			EndedAtMs = null,
			TranscriptItemId = null,
		};

		Assert.Equal(listed, item.Listed);
	}
}
