namespace Weavie.Core.Agents;

/// <summary>A provider-neutral update for Weavie's native agent pane.</summary>
public sealed record AgentPaneMessage {
	/// <summary>The web-facing message kind.</summary>
	public required string Type { get; init; }

	/// <summary>The provider id that produced the update.</summary>
	public required string ProviderId { get; init; }

	/// <summary>The current provider thread id, when known.</summary>
	public string? ThreadId { get; init; }

	/// <summary>Whether this update belongs to the session's primary thread, when reported.</summary>
	public bool? IsPrimaryThread { get; init; }

	/// <summary>The current provider turn id, when known.</summary>
	public string? TurnId { get; init; }

	/// <summary>The provider-recorded turn start as Unix milliseconds, when available.</summary>
	public long? StartedAtMs { get; init; }

	/// <summary>The item id associated with this update, when any.</summary>
	public string? ItemId { get; init; }

	/// <summary>The provider request id associated with an interactive pane item, when any.</summary>
	public string? RequestId { get; init; }

	/// <summary>The structured item kind, when any.</summary>
	public string? ItemType { get; init; }

	/// <summary>The authoritative item set associated with a level update, when any.</summary>
	public IReadOnlyList<string>? ItemIds { get; init; }

	/// <summary>The provider-neutral presentation category for this update.</summary>
	public string? Category { get; init; }

	/// <summary>A concise user-facing summary for list/card rendering.</summary>
	public string? Summary { get; init; }

	/// <summary>Streaming or final text content, when any.</summary>
	public string? Text { get; init; }

	/// <summary>A status value from the provider contract.</summary>
	public string? Status { get; init; }

	/// <summary>Normalized questions for an input request, when any.</summary>
	public IReadOnlyList<AgentInputQuestion>? Questions { get; init; }

	/// <summary>The exact provider-advertised actions for a permission, authentication, or elicitation request.</summary>
	public IReadOnlyList<AgentActionOption>? Actions { get; init; }

	/// <summary>File locations associated with this update.</summary>
	public IReadOnlyList<AgentPaneLocation>? Locations { get; init; }

	/// <summary>Structured diffs associated with this update.</summary>
	public IReadOnlyList<AgentPaneDiff>? Diffs { get; init; }

	/// <summary>Ordered rich content associated with a tool result.</summary>
	public IReadOnlyList<AgentPaneContent>? Content { get; init; }

	/// <summary>The parent tool call for a nested subagent update, when advertised.</summary>
	public string? ParentItemId { get; init; }

	/// <summary>Whether the item represents background or subagent work.</summary>
	public bool? Background { get; init; }

	/// <summary>The terminal referenced by a tool update, when any.</summary>
	public string? TerminalId { get; init; }

	/// <summary>Context-window tokens currently used, when reported.</summary>
	public long? UsageUsed { get; init; }

	/// <summary>Total context-window size, when reported.</summary>
	public long? UsageSize { get; init; }

	/// <summary>The MIME type for inline media content.</summary>
	public string? MediaType { get; init; }

	/// <summary>Base64-encoded inline media content.</summary>
	public string? MediaData { get; init; }

	/// <summary>A linked resource URI associated with the content.</summary>
	public string? ResourceUri { get; init; }

}

/// <summary>One ordered rich-content block rendered from an agent tool result.</summary>
public sealed record AgentPaneContent {
	/// <summary>The stable ACP content-block kind.</summary>
	public required string Type { get; init; }

	/// <summary>Text carried by the block, when any.</summary>
	public string? Text { get; init; }

	/// <summary>The MIME type for inline binary content.</summary>
	public string? MediaType { get; init; }

	/// <summary>Base64-encoded inline binary content.</summary>
	public string? MediaData { get; init; }

	/// <summary>The resource URI carried by the block, when any.</summary>
	public string? ResourceUri { get; init; }

	/// <summary>The user-facing resource name, when any.</summary>
	public string? Name { get; init; }
}

/// <summary>One exact action advertised by an agent interaction.</summary>
public sealed record AgentActionOption {
	/// <summary>The opaque value echoed to the provider.</summary>
	public required string Id { get; init; }

	/// <summary>The user-facing action label.</summary>
	public required string Label { get; init; }

	/// <summary>The protocol hint for action presentation.</summary>
	public required string Kind { get; init; }
}

/// <summary>A file location reported by an agent tool.</summary>
public sealed record AgentPaneLocation {
	/// <summary>The absolute file path.</summary>
	public required string Path { get; init; }

	/// <summary>The optional one-based line number.</summary>
	public long? Line { get; init; }
}

/// <summary>A structured file diff reported by an agent tool.</summary>
public sealed record AgentPaneDiff {
	/// <summary>The absolute file path.</summary>
	public required string Path { get; init; }

	/// <summary>The file contents before the tool change, when supplied.</summary>
	public string? OldText { get; init; }

	/// <summary>The file contents after the tool change.</summary>
	public required string NewText { get; init; }
}
