namespace Weavie.Core.Agents;

/// <summary>
/// A guided read of how some code works: the problem, a short answer, then stops through real code in the order it
/// runs. Code is held as anchors into copies Weavie keeps, never as text the agent sent. See
/// docs/specs/code-walkthroughs/design.md.
/// </summary>
public sealed record AgentWalkthrough {
	/// <summary>What the code has to solve and the question that raises, as markdown.</summary>
	public required string Problem { get; init; }

	/// <summary>The one-paragraph answer, as markdown.</summary>
	public required string Gist { get; init; }

	/// <summary>The concrete example followed through the stops.</summary>
	public string? Example { get; init; }

	/// <summary>Markdown after the last stop.</summary>
	public string? Closing { get; init; }

	/// <summary>What the agent changed, set by the first update.</summary>
	public string? Change { get; init; }

	/// <summary>The stops, in reading order.</summary>
	public required IReadOnlyList<AgentWalkthroughStop> Stops { get; init; }

	/// <summary>Every cited file and the copies of it the walkthrough keeps.</summary>
	public required IReadOnlyList<AgentWalkthroughFile> Files { get; init; }
}

/// <summary>A cited file: its first recorded copy and, after an update, the copy that update recorded.</summary>
public sealed record AgentWalkthroughFile {
	/// <summary>The worktree-relative path.</summary>
	public required string Path { get; init; }

	/// <summary>The hash of the first copy recorded; every block on the file diffs from it.</summary>
	public required string Before { get; init; }

	/// <summary>The hash of the copy the last update recorded; empty when the file was gone; null before an update.</summary>
	public string? After { get; init; }

	/// <summary>Whether the first copy was taken while the file held agent edits the user had not kept.</summary>
	public bool Late { get; init; }
}

/// <summary>One stop of the read.</summary>
public sealed record AgentWalkthroughStop {
	/// <summary>The agent-chosen id, unique within the walkthrough.</summary>
	public required string Id { get; init; }

	/// <summary>The short label in the strip of stops.</summary>
	public required string Name { get; init; }

	/// <summary>The stop's heading.</summary>
	public required string Title { get; init; }

	/// <summary>What to read the stop's code for, as markdown.</summary>
	public required string Lead { get; init; }

	/// <summary>The stop's code blocks.</summary>
	public required IReadOnlyList<AgentWalkthroughCode> Code { get; init; }

	/// <summary>The followed example after this stop.</summary>
	public AgentWalkthroughState? State { get; init; }

	/// <summary>Why the code is built this way.</summary>
	public AgentWalkthroughWhy? Why { get; init; }

	/// <summary>A question for the reader, with its answer.</summary>
	public AgentWalkthroughPredict? Predict { get; init; }

	/// <summary><c>changed</c>, <c>affected</c> or <c>added</c> after an update; null on a stop no update touched.</summary>
	public string? Effect { get; init; }

	/// <summary>What the update changed about the stop.</summary>
	public string? Note { get; init; }
}

/// <summary>A block of real code: a line range of a cited file, with folds and notes.</summary>
public sealed record AgentWalkthroughCode {
	/// <summary>The cited file's path.</summary>
	public required string Path { get; init; }

	/// <summary>The first line shown, 1-based.</summary>
	public required int From { get; init; }

	/// <summary>The last line shown.</summary>
	public required int To { get; init; }

	/// <summary>The hash of the copy the block was checked against; Weavie sets it.</summary>
	public string? Source { get; init; }

	/// <summary>Line ranges hidden behind a labelled row until opened.</summary>
	public IReadOnlyList<AgentWalkthroughFold>? Folds { get; init; }

	/// <summary>Notes under lines.</summary>
	public IReadOnlyList<AgentWalkthroughNote>? Notes { get; init; }
}

/// <summary>A folded line range and what it holds.</summary>
public sealed record AgentWalkthroughFold {
	/// <summary>The first folded line.</summary>
	public required int From { get; init; }

	/// <summary>The last folded line.</summary>
	public required int To { get; init; }

	/// <summary>What the folded lines hold.</summary>
	public required string Label { get; init; }
}

/// <summary>A note under a line, about a token on it.</summary>
public sealed record AgentWalkthroughNote {
	/// <summary>The line, in the block's copy.</summary>
	public required int Line { get; init; }

	/// <summary>Text on the line the note is about.</summary>
	public required string Token { get; init; }

	/// <summary>The note.</summary>
	public required string Text { get; init; }

	/// <summary>The note's earlier text, when an update reworded it; Weavie sets it.</summary>
	public string? Was { get; init; }
}

/// <summary>The followed example as it stands after a stop: the agent's account, drawn as prose.</summary>
public sealed record AgentWalkthroughState {
	/// <summary>Labelled values, side by side.</summary>
	public required IReadOnlyList<AgentWalkthroughCell> Cells { get; init; }

	/// <summary>A line under the values.</summary>
	public string? Note { get; init; }
}

/// <summary>One labelled value of the example's state; empty reads as nothing.</summary>
public sealed record AgentWalkthroughCell {
	/// <summary>What the value is.</summary>
	public required string Label { get; init; }

	/// <summary>The value.</summary>
	public required string Value { get; init; }
}

/// <summary>Why the code is built this way, and where that is known from.</summary>
public sealed record AgentWalkthroughWhy {
	/// <summary>The question it answers.</summary>
	public required string Title { get; init; }

	/// <summary>The answer, as markdown.</summary>
	public required string Text { get; init; }

	/// <summary>Where the answer comes from.</summary>
	public required AgentWalkthroughSource Source { get; init; }
}

/// <summary>Where an explanation comes from.</summary>
public sealed record AgentWalkthroughSource {
	/// <summary><c>spec</c>, <c>pr</c>, <c>commit</c>, or <c>reading</c> (the agent's own reading of the code).</summary>
	public required string Kind { get; init; }

	/// <summary>The spec path, PR, or commit; absent for a reading.</summary>
	public string? Ref { get; init; }
}

/// <summary>A question the reader answers before reading on.</summary>
public sealed record AgentWalkthroughPredict {
	/// <summary>The question.</summary>
	public required string Question { get; init; }

	/// <summary>The answer, hidden until asked for.</summary>
	public required string Answer { get; init; }
}

/// <summary>What publishWalkthrough and updateWalkthrough send.</summary>
public sealed record AgentWalkthroughRequest {
	/// <summary>The walkthrough's block id.</summary>
	public required string Id { get; init; }

	/// <summary>The title.</summary>
	public string? Title { get; init; }

	/// <summary>See <see cref="AgentWalkthrough.Problem"/>.</summary>
	public string? Problem { get; init; }

	/// <summary>See <see cref="AgentWalkthrough.Gist"/>.</summary>
	public string? Gist { get; init; }

	/// <summary>See <see cref="AgentWalkthrough.Example"/>.</summary>
	public string? Example { get; init; }

	/// <summary>See <see cref="AgentWalkthrough.Closing"/>.</summary>
	public string? Closing { get; init; }

	/// <summary>What the agent changed; required to update.</summary>
	public string? Change { get; init; }

	/// <summary>The stops to publish, or one change each to update.</summary>
	public IReadOnlyList<AgentWalkthroughStopRequest>? Stops { get; init; }
}

/// <summary>A stop to publish or add, or the fields of one to change.</summary>
public sealed record AgentWalkthroughStopRequest {
	/// <summary>The stop id.</summary>
	public required string Id { get; init; }

	/// <summary>The stop an added stop follows; it goes last without one.</summary>
	public string? After { get; init; }

	/// <summary>See <see cref="AgentWalkthroughStop.Name"/>.</summary>
	public string? Name { get; init; }

	/// <summary>See <see cref="AgentWalkthroughStop.Title"/>.</summary>
	public string? Title { get; init; }

	/// <summary>See <see cref="AgentWalkthroughStop.Lead"/>.</summary>
	public string? Lead { get; init; }

	/// <summary>See <see cref="AgentWalkthroughStop.Code"/>; given on a change, it replaces every block.</summary>
	public IReadOnlyList<AgentWalkthroughCode>? Code { get; init; }

	/// <summary>See <see cref="AgentWalkthroughStop.State"/>.</summary>
	public AgentWalkthroughState? State { get; init; }

	/// <summary>See <see cref="AgentWalkthroughStop.Why"/>.</summary>
	public AgentWalkthroughWhy? Why { get; init; }

	/// <summary>See <see cref="AgentWalkthroughStop.Predict"/>.</summary>
	public AgentWalkthroughPredict? Predict { get; init; }

	/// <summary><c>changed</c> or <c>affected</c>; required to change a stop.</summary>
	public string? Effect { get; init; }

	/// <summary>What changed about the stop; required to change it.</summary>
	public string? Note { get; init; }
}

/// <summary>One change an update makes, applied in order to the walkthrough as it stands at the commit.</summary>
public abstract record AgentWalkthroughChange {
	/// <summary>Replaces each text given; null keeps the current text.</summary>
	public sealed record SetText(string? Title, string? Problem, string? Gist, string? Example, string? Closing, string Change) : AgentWalkthroughChange;

	/// <summary>Adds a stop after <paramref name="After"/>, or last.</summary>
	public sealed record AddStop(AgentWalkthroughStop Stop, string? After) : AgentWalkthroughChange;

	/// <summary>Changes the fields <paramref name="Request"/> gives; <paramref name="Code"/> is its checked code.</summary>
	public sealed record PatchStop(AgentWalkthroughStopRequest Request, IReadOnlyList<AgentWalkthroughCode>? Code) : AgentWalkthroughChange;

	/// <summary>Records each file as it stood at the update.</summary>
	public sealed record RecordFiles(IReadOnlyList<AgentWalkthroughFile> Files) : AgentWalkthroughChange;
}

/// <summary>The bounds a walkthrough must fit; a request over one is refused with the number.</summary>
public static class AgentWalkthroughLimits {
	/// <summary>Stops per walkthrough.</summary>
	public const int Stops = 24;
	/// <summary>Code blocks per stop.</summary>
	public const int Blocks = 6;
	/// <summary>Lines per code block.</summary>
	public const int Lines = 400;
	/// <summary>Folds per code block.</summary>
	public const int Folds = 12;
	/// <summary>Notes per code block.</summary>
	public const int Notes = 24;
	/// <summary>Values in a stop's state.</summary>
	public const int Cells = 4;
	/// <summary>Characters in a stop's name.</summary>
	public const int Name = 24;
	/// <summary>Characters in a title, a fold label, or a note.</summary>
	public const int Title = 200;
	/// <summary>Characters in a state value.</summary>
	public const int Value = 300;
	/// <summary>Characters in any other text.</summary>
	public const int Text = 4000;
	/// <summary>The size of a cited file, in UTF-8 bytes.</summary>
	public const int FileBytes = 2 * 1024 * 1024;
}
