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

/// <summary>An atomic structured-agent input: text and the exact staged images that belong to the turn.</summary>
public sealed record AgentTurnSubmission {
	/// <summary>The client-generated submission id.</summary>
	public required string Id { get; init; }

	/// <summary>The prompt text; empty is valid when at least one attachment is present.</summary>
	public required string Text { get; init; }

	/// <summary>The staged images submitted with the text.</summary>
	public required IReadOnlyList<AgentInputAttachment> Attachments { get; init; }
}
