using Weavie.Core.Agents;
using Weavie.Workflows;

namespace Weavie.Core.Workflows;

/// <summary>Maps the turns a run owes onto agent turn submissions.</summary>
public static class WorkflowKickoffTurns {
	/// <summary>The agent turn that delivers <paramref name="kickoff"/>; a skill's command turn carries the stage block too.</summary>
	public static AgentTurnSubmission Submission(this WorkflowKickoff kickoff) => new() {
		Id = Guid.NewGuid().ToString("n"),
		Text = kickoff.Text,
		Kind = kickoff.Command.Length > 0 ? AgentTurnSubmissionKind.ProviderCommand : AgentTurnSubmissionKind.Prompt,
		CommandName = kickoff.Command,
		Attachments = [],
		CarriesTurnContext = true,
	};
}
