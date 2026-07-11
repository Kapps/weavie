using System.Text.Json.Serialization;

namespace Weavie.Core.Corrections;

/// <summary>One corrected file within a <see cref="CorrectionRecord"/>.</summary>
public sealed record CorrectionFile {
	/// <summary>The file's workspace-root-relative path with <c>/</c> separators.</summary>
	[JsonPropertyName("path")]
	public required string Path { get; init; }

	/// <summary>The unified diff of the agent's output → the user's corrected content.</summary>
	[JsonPropertyName("delta")]
	public required string Delta { get; init; }
}

/// <summary>
/// One turn's worth of out-of-band corrections: the prompt that produced the corrected output (when the
/// provider reports one) plus a delta per corrected file. Serializes as one JSONL line in the
/// <see cref="CorrectionCorpus"/>; the corpus stores these raw — all reasoning over them is Claude's.
/// </summary>
public sealed record CorrectionRecord {
	/// <summary>The user prompt whose output was corrected; <see langword="null"/> when the provider carries none (Codex).</summary>
	[JsonPropertyName("prompt")]
	public string? Prompt { get; init; }

	/// <summary>The corrected files, each with its agent-output → final-content delta.</summary>
	[JsonPropertyName("files")]
	public required IReadOnlyList<CorrectionFile> Files { get; init; }

	/// <summary>How many additional corrected files were dropped to fit the per-entry byte ceiling.</summary>
	[JsonPropertyName("droppedFiles")]
	public int DroppedFiles { get; init; }
}
