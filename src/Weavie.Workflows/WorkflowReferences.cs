namespace Weavie.Workflows;

internal static class WorkflowReferences {
	/// <summary>The names a repeated stage gives its current entry.</summary>
	public static IReadOnlyList<string> EntryNames { get; } = ["item", "index"];

	/// <summary>Whether <paramref name="reference"/> names the current entry of a repeated stage.</summary>
	public static bool IsEntry(WorkflowDefinition definition, WorkflowReference reference) =>
		EntryNames.Contains(reference.Name) && definition.TryStage(reference.Scope)?.Repeats == true;

	/// <summary>
	/// Whether <paramref name="reference"/> reads a repeated stage's output, directly or copied by a stage holding it. Only
	/// a copy from one of the stage's own stages is followed, so the walk always descends the tree and ends.
	/// </summary>
	public static bool IsRepeated(WorkflowDefinition definition, WorkflowReference reference) =>
		!IsEntry(definition, reference) && definition.TryStage(reference.Scope) is { } stage
		&& (stage.Repeats || stage.Outputs.FirstOrDefault(o => o.Name == reference.Name)?.Source is { } source
			&& stage.Copied(source) is not null && IsRepeated(definition, source));

	/// <summary>The type <paramref name="reference"/> reads, or null when it names nothing; a repeated output has its entries' type.</summary>
	public static WorkflowValueType? TypeOf(WorkflowDefinition definition, WorkflowReference reference) =>
		reference.IsDocuments || reference.IsPrompt || IsEntry(definition, reference) ? WorkflowValueType.Text
		: reference.IsInput ? definition.Inputs.FirstOrDefault(input => input.Name == reference.Name)?.Type
		: definition.TryStage(reference.Scope)?.Outputs.FirstOrDefault(output => output.Name == reference.Name)?.Type;
}
