namespace Weavie.Workflows;

internal static class WorkflowReferences {
	public static WorkflowValueType? TypeOf(WorkflowDefinition definition, WorkflowReference reference) =>
		reference.IsDocuments ? WorkflowValueType.Text : reference.IsInput
			? definition.Inputs.FirstOrDefault(input => input.Name == reference.Name)?.Type
			: definition.Stages.FirstOrDefault(stage => stage.Id == reference.Scope)?.Run.Outputs
				.FirstOrDefault(output => output.Name == reference.Name)?.Type;
}
