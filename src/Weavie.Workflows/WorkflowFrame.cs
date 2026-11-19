namespace Weavie.Workflows;

/// <summary>
/// One instance of a level of the run: the root, a stage with stages, or one entry of a repeated stage. It owns its
/// stages' outputs, its decisions' counts, the reports owed to its conversations, and those conversations; discarding
/// it ends all of them. See docs/specs/workflow-nested-stages/design.md.
/// </summary>
public sealed record WorkflowFrame {
	/// <summary>The record's id: the run id, a slash, and a number unique within the run.</summary>
	public required string Id { get; init; }

	/// <summary>The id of the record holding this one, or empty for the root.</summary>
	public required string Parent { get; init; }

	/// <summary>The stage this record is an instance of, or empty for the root.</summary>
	public required string Stage { get; init; }

	/// <summary>The entry's number from 1 when the stage repeats; otherwise 0.</summary>
	public required int Entry { get; init; }

	/// <summary>The entry's item when the stage repeats; otherwise empty.</summary>
	public required string Item { get; init; }

	/// <summary>The conversation its stages with no context run in, or <see langword="null"/> for main.</summary>
	public required WorkflowConversationRef? Conversation { get; init; }

	/// <summary>The outputs of the stages in this record's list, by stage id.</summary>
	public required Dictionary<string, Dictionary<string, WorkflowValue>> Outputs { get; init; }

	/// <summary>The repeated stages in this record's list whose entries are running, by stage id.</summary>
	public required Dictionary<string, WorkflowLoop> Loops { get; init; }

	/// <summary>The model's consecutive answers to a <c>then</c> decision in this record's list, by stage id.</summary>
	public required Dictionary<string, int> Streaks { get; init; }

	/// <summary>Completed stages' reports owed to a conversation this record owns, by opening stage; empty names main, on the root.</summary>
	public required Dictionary<string, string> Owed { get; init; }

	/// <summary>The opening stages whose conversation in this record is in use for this pass: opened for a turn or to be forked.</summary>
	public required IReadOnlyList<string> Opened { get; init; }

	/// <summary>The opening stages whose conversation in this record has taken a turn of this pass.</summary>
	public required IReadOnlyList<string> Prompted { get; init; }

	/// <summary>Whether this record is one entry of repeated stage <paramref name="stage"/>.</summary>
	public bool IsEntryOf(string stage) => Entry > 0 && Stage == stage;
}

/// <summary>A repeated stage's captured list and the outputs of its finished entries.</summary>
public sealed record WorkflowLoop {
	/// <summary>The entries, captured when the stage started.</summary>
	public required IReadOnlyList<string> Items { get; init; }

	/// <summary>Each finished entry's outputs, in order.</summary>
	public required IReadOnlyList<Dictionary<string, WorkflowValue>> Results { get; init; }
}

/// <summary>A stage conversation: the record that owns it and the stage that opens it.</summary>
public sealed record WorkflowConversationRef(string Frame, string Opener);
