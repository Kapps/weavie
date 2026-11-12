using Weavie.Core.Agents;
using Weavie.Workflows;

namespace Weavie.Core.Workflows;

/// <summary>Maps the turns a run owes onto agent turn submissions.</summary>
public static class WorkflowKickoffTurns {
	/// <summary>The agent turn that delivers <paramref name="kickoff"/> with <paramref name="images"/>; a skill's command turn carries the stage block too.</summary>
	public static AgentTurnSubmission Submission(this WorkflowKickoff kickoff, IReadOnlyList<WorkflowTurnImage> images) => new() {
		Id = Guid.NewGuid().ToString("n"),
		Text = kickoff.Text,
		Kind = kickoff.Command.Length > 0 ? AgentTurnSubmissionKind.ProviderCommand : AgentTurnSubmissionKind.Prompt,
		CommandName = kickoff.Command,
		Attachments = [.. images.Select(image => new AgentInputAttachment.Image { Id = Guid.NewGuid().ToString("n"), Mime = image.Mime, Path = image.Path })],
		CarriesTurnContext = true,
	};
}
