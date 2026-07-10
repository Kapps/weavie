namespace Weavie.Core.Agents;

/// <summary>A provider-neutral question shown by a structured agent request.</summary>
public sealed record AgentInputQuestion {
	/// <summary>The stable answer key expected by the provider.</summary>
	public required string Id { get; init; }

	/// <summary>A short label for the question.</summary>
	public required string Header { get; init; }

	/// <summary>The full prompt shown to the user.</summary>
	public required string Question { get; init; }

	/// <summary>Whether the answer must be hidden while it is entered.</summary>
	public required bool IsSecret { get; init; }

	/// <summary>The available choices; empty means free-form input.</summary>
	public required IReadOnlyList<AgentInputOption> Options { get; init; }
}

/// <summary>One provider-neutral choice for an agent input question.</summary>
public sealed record AgentInputOption {
	/// <summary>The value and label returned when this choice is selected.</summary>
	public required string Label { get; init; }

	/// <summary>Additional guidance about the choice.</summary>
	public required string Description { get; init; }
}
