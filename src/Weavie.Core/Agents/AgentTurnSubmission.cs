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

/// <summary>An atomic structured-agent input: text, the exact staged images, and any staged skills for the turn.</summary>
public sealed record AgentTurnSubmission {
	/// <summary>The client-generated submission id.</summary>
	public required string Id { get; init; }

	/// <summary>The prompt text; empty is valid when at least one attachment or skill is present.</summary>
	public required string Text { get; init; }

	/// <summary>The staged images submitted with the text.</summary>
	public required IReadOnlyList<AgentInputAttachment> Attachments { get; init; }

	/// <summary>The provider skill names staged for this turn; the provider resolves each to a structured skill input.</summary>
	public required IReadOnlyList<string> Skills { get; init; }
}
