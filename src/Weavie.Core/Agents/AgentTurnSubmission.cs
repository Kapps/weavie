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
}

/// <summary>An atomic structured-agent input: text and the exact staged images for the turn.</summary>
public sealed record AgentTurnSubmission {
	/// <summary>The client-generated submission id.</summary>
	public required string Id { get; init; }

	/// <summary>The prompt text; empty is valid when at least one attachment is present.</summary>
	public required string Text { get; init; }

	/// <summary>Whether the text is an ordinary prompt or a provider-advertised command.</summary>
	public required AgentTurnSubmissionKind Kind { get; init; }

	/// <summary>The provider's exact advertised command name; empty for an ordinary prompt.</summary>
	public required string CommandName { get; init; }

	/// <summary>The staged images submitted with the text.</summary>
	public required IReadOnlyList<AgentInputAttachment> Attachments { get; init; }
}
