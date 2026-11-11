namespace Weavie.Core.Agents;

/// <summary>One image attached to a structured agent turn after the owning host has staged it on disk.</summary>
public sealed record AgentInputAttachment {
	/// <summary>The client-generated attachment id, scoped to one Weavie session.</summary>
	public required string Id { get; init; }

	/// <summary>The absolute path on the provider's host.</summary>
	public required string Path { get; init; }

	/// <summary>The validated image MIME type.</summary>
	public required string Mime { get; init; }
}

/// <summary>The semantic kind of one structured-agent submission.</summary>
public enum AgentTurnSubmissionKind {
	/// <summary>An ordinary user prompt.</summary>
	Prompt,

	/// <summary>One provider-advertised slash command.</summary>
	ProviderCommand,

	/// <summary>A bundled MCP prompt selected by the user.</summary>
	McpPrompt,
}

/// <summary>
/// The Weavie session whose agent sent a message. <paramref name="SameSession"/> marks one sent from inside the
/// receiving session, whose conversations all share its identity.
/// </summary>
public sealed record AgentMessageSender(string Id, string Title, bool SameSession);

/// <summary>An atomic structured-agent input: text and the exact staged images for the turn.</summary>
public sealed record AgentTurnSubmission {
	/// <summary>An ordinary text prompt with a fresh id.</summary>
	public static AgentTurnSubmission Prompt(string text) => new() {
		Id = Guid.NewGuid().ToString("n"),
		Text = text,
		Kind = AgentTurnSubmissionKind.Prompt,
		CommandName = string.Empty,
		Attachments = [],
	};

	/// <summary>The client-generated submission id.</summary>
	public required string Id { get; init; }

	/// <summary>The prompt text; empty is valid when at least one attachment is present.</summary>
	public required string Text { get; init; }

	/// <summary>Whether the text is an ordinary prompt or a provider-advertised command.</summary>
	public required AgentTurnSubmissionKind Kind { get; init; }

	/// <summary>The exact provider command or bundled MCP prompt name; empty for an ordinary prompt.</summary>
	public required string CommandName { get; init; }

	/// <summary>The staged images submitted with the text.</summary>
	public required IReadOnlyList<AgentInputAttachment> Attachments { get; init; }

	/// <summary>The session that sent this prompt, when an agent sent it with messageSession rather than the user.</summary>
	public AgentMessageSender? Sender { get; init; }

	/// <summary>Whether a provider command also carries the session's per-turn context, as a workflow stage's opening
	/// turn does; ordinary prompts always carry it.</summary>
	public bool CarriesTurnContext { get; init; }

	/// <summary>
	/// The turn as a terminal agent takes it: the text and each image's path, which the agent attaches. A command's
	/// text leads so its slash opens the input; otherwise the images lead the text.
	/// </summary>
	public IReadOnlyList<string> TerminalParts() {
		var paths = Attachments.Select(attachment => attachment.Path);
		return Kind == AgentTurnSubmissionKind.ProviderCommand ? [Text, .. paths] : [.. paths, Text];
	}
}
