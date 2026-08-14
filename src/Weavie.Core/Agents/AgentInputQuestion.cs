namespace Weavie.Core.Agents;

/// <summary>A provider-neutral question shown by a structured agent request.</summary>
public sealed record AgentInputQuestion {
	/// <summary>The stable answer key expected by the provider.</summary>
	public required string Id { get; init; }

	/// <summary>A short label for the question.</summary>
	public required string Header { get; init; }

	/// <summary>The full prompt shown to the user.</summary>
	public required string Question { get; init; }

	/// <summary>Whether the provider accepts a free-form answer in addition to its advertised choices.</summary>
	public required bool AllowsOther { get; init; }

	/// <summary>The ACP-compatible primitive input kind.</summary>
	public required string Kind { get; init; }

	/// <summary>Whether the provider requires an answer.</summary>
	public required bool Required { get; init; }

	/// <summary>The optional string format hint.</summary>
	public required string? Format { get; init; }

	/// <summary>The initial wire values represented as strings for editing.</summary>
	public required IReadOnlyList<string> InitialValues { get; init; }

	/// <summary>The inclusive numeric lower bound.</summary>
	public required double? Minimum { get; init; }

	/// <summary>The inclusive numeric upper bound.</summary>
	public required double? Maximum { get; init; }

	/// <summary>The minimum string or selection length.</summary>
	public required int? MinimumLength { get; init; }

	/// <summary>The maximum string or selection length.</summary>
	public required int? MaximumLength { get; init; }

	/// <summary>The optional string validation pattern.</summary>
	public required string? Pattern { get; init; }

	/// <summary>The available choices; empty means free-form input.</summary>
	public required IReadOnlyList<AgentInputOption> Options { get; init; }
}

/// <summary>One provider-neutral choice for an agent input question.</summary>
public sealed record AgentInputOption {
	/// <summary>The opaque value returned to the provider.</summary>
	public required string Value { get; init; }

	/// <summary>The human-readable choice label.</summary>
	public required string Label { get; init; }

	/// <summary>Additional guidance about the choice.</summary>
	public required string Description { get; init; }
}
